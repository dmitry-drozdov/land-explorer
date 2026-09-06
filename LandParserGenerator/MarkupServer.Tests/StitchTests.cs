using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты сшивки Id у auto-якорей при последовательных вызовах ListAnchors.
///
/// Цель: убедиться, что Id у auto-якорей переживает forceRescan, когда
/// семантическая идентичность якоря не поменялась. Это предусловие для
/// будущих user-групп / пользовательских ссылок.
///
/// На MVP включён только слой 1 сшивки (точный матч по ключу
/// lang|kind|filepath|parent|method). Слой 2 (VP-tree) выключен порогом=0
/// и тестируется отдельно — в этом файле его поведение не проверяется
/// помимо документирующего теста про rename.
/// </summary>
[TestClass]
public class StitchTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempFolder);
	}

	[TestCleanup]
	public void Cleanup()
	{
		try
		{
			if (!string.IsNullOrEmpty(_tempFolder) && Directory.Exists(_tempFolder))
				Directory.Delete(_tempFolder, true);
		}
		catch
		{
			// best effort
		}
	}

	private static async Task<LandService> CreateInitializedService()
	{
		var svc = new LandService();
		await svc.InitializeAsync(new InitializeParams
		{
			protocolVersion = 2,
			offsetEncoding = "utf-16",
			graphqlParserPath = Path.Combine(GrammarsFolder, "graphql.land"),
			typescriptParserPath = Path.Combine(GrammarsFolder, "ts_resolvers.land"),
		});
		return svc;
	}

	private void CopyFixtureInto(string fixtureName)
	{
		var src = Path.Combine(FixturesFolder, fixtureName);
		if (!Directory.Exists(src))
			throw new DirectoryNotFoundException($"Fixture '{fixtureName}' not found at {src}");

		// Удаляем все файлы кроме .land/anchors.json (он должен переживать переключения фикстур)
		foreach (var f in Directory.GetFiles(_tempFolder, "*", SearchOption.AllDirectories))
		{
			var rel = Path.GetRelativePath(_tempFolder, f).Replace('\\', '/');
			if (rel.StartsWith(".land/", StringComparison.OrdinalIgnoreCase)) continue;
			File.Delete(f);
		}

		foreach (var srcFile in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
		{
			var rel = Path.GetRelativePath(src, srcFile);
			var dst = Path.Combine(_tempFolder, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			File.Copy(srcFile, dst, true);
		}
	}

	private async Task<List<TreeNodeClientV2>> ListAnchorsAsync(LandService svc, bool forceRescan = true)
	{
		var res = await svc.ListAnchorsAsync(new ListTreeParams
		{
			folderPath = _tempFolder,
			forceRescan = forceRescan,
			preferCache = !forceRescan,
		});

		var sink = new List<TreeNodeClientV2>();
		CollectAnchors(res?.r, sink);
		return sink;
	}

	private static void CollectAnchors(List<TreeNodeClientV2> nodes, List<TreeNodeClientV2> sink)
	{
		if (nodes == null) return;
		foreach (var n in nodes)
		{
			if (n == null) continue;
			if (string.Equals(n.t, "anchor", StringComparison.OrdinalIgnoreCase))
				sink.Add(n);
			if (n.c != null) CollectAnchors(n.c, sink);
		}
	}

	/// <summary>
	/// Ключ для сопоставления anchor-узлов между двумя snapshot-ами по их видимым полям
	/// (без Id). Используется для проверок типа "тот же anchor по семантике, но Id мог поменяться".
	/// </summary>
	private static string AnchorVisibleKey(TreeNodeClientV2 a)
	{
		// Используем имя + базовое имя файла + kind. Этого достаточно для нашего тестового набора фикстур,
		// потому что в одной фикстуре нет двух anchor-узлов с одинаковыми (name, file, kind).
		var fileBase = string.IsNullOrEmpty(a.f) ? "" : Path.GetFileName(a.f);
		return $"{a.k}|{fileBase}|{a.n}";
	}

	// =========================================================================
	// 1. Повторный listAnchors без правок: все Id сохраняются.
	// =========================================================================
	[TestMethod]
	public async Task Listing_Twice_Without_Changes_Preserves_All_Ids()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();

		var first = await ListAnchorsAsync(svc);
		var second = await ListAnchorsAsync(svc);

		Assert.IsTrue(first.Count > 0, "fixture must produce some anchors");
		Assert.AreEqual(first.Count, second.Count, "anchor count must match between rescans");

		var firstIds = first.Select(x => x.id).OrderBy(x => x, StringComparer.Ordinal).ToList();
		var secondIds = second.Select(x => x.id).OrderBy(x => x, StringComparer.Ordinal).ToList();
		CollectionAssert.AreEqual(firstIds, secondIds, "all anchor Ids must survive a no-op rescan");
	}

	// =========================================================================
	// 2. Изменения, не затрагивающие семантический ключ (сдвиг offsets):
	// слой 1 матчит по (lang|kind|file|parent|method), Id сохраняются,
	// при этом offsets обновляются.
	// =========================================================================
	[TestMethod]
	public async Task Listing_With_Offset_Shift_Preserves_Ids_And_Updates_Offsets()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();

		var first = await ListAnchorsAsync(svc);

		CopyFixtureInto("shifted");
		var second = await ListAnchorsAsync(svc);

		Assert.AreEqual(first.Count, second.Count, "anchor count must match (semantics unchanged)");

		var firstByKey = first.ToDictionary(AnchorVisibleKey);
		foreach (var s in second)
		{
			var key = AnchorVisibleKey(s);
			Assert.IsTrue(firstByKey.TryGetValue(key, out var f),
				$"anchor with visible-key '{key}' missing from first listing");
			Assert.AreEqual(f.id, s.id, $"Id of '{key}' must be preserved across offset shift");
		}

		// Проверим, что хотя бы у одного якоря offset действительно сдвинулся (фикстура работает).
		Assert.IsTrue(
			second.Any(s => s.s != firstByKey[AnchorVisibleKey(s)].s),
			"shifted fixture is supposed to push at least one anchor's start offset forward");
	}

	// =========================================================================
	// 3. Удаление одного метода: его Id уходит в lost, остальные Id сохраняются.
	// =========================================================================
	[TestMethod]
	public async Task Listing_After_Method_Removal_Drops_Its_Id_But_Keeps_The_Rest()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();

		var first = await ListAnchorsAsync(svc);
		var firstByKey = first.ToDictionary(AnchorVisibleKey);

		CopyFixtureInto("removed");
		var second = await ListAnchorsAsync(svc);

		// gqlField listUsers должен исчезнуть.
		// (Одноимённый ts-резолвер сохраняется — это нормально, мы трогаем только schema.graphql.)
		Assert.IsFalse(second.Any(x => x.n == "listUsers" && x.k == "gqlField"),
			"listUsers gqlField anchor must be absent after removal");

		// Все остальные anchor-Id из первого snapshot должны быть в новом, кроме gqlField listUsers.
		foreach (var f in first)
		{
			if (f.n == "listUsers" && f.k == "gqlField") continue;
			var key = AnchorVisibleKey(f);
			var match = second.FirstOrDefault(s => AnchorVisibleKey(s) == key);
			Assert.IsNotNull(match, $"anchor '{key}' must remain after listUsers removal");
			Assert.AreEqual(f.id, match.id, $"Id of '{key}' must be preserved");
		}
	}

	// =========================================================================
	// 4. Слой 2 (VP-дерево, tau + margin) включён по умолчанию с сентября 2026:
	// переименованное поле getUser -> findUserById (те же аргументы, тип и
	// родитель) сшивается и наследует старый Id. До этого порог был 0 и тест
	// фиксировал противоположное ожидание.
	// =========================================================================
	[TestMethod]
	public async Task Listing_After_Rename_Stitches_Id_Through_Fuzzy_Layer()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();

		var first = await ListAnchorsAsync(svc);
		var oldGetUser = first.FirstOrDefault(x => x.n == "getUser");
		Assert.IsNotNull(oldGetUser, "basic fixture must have getUser");

		CopyFixtureInto("rename");
		var second = await ListAnchorsAsync(svc);

		var newAnchor = second.FirstOrDefault(x => x.n == "findUserById");
		Assert.IsNotNull(newAnchor, "rename fixture must have findUserById");

		Assert.AreEqual(oldGetUser.id, newAnchor.id,
			"renamed anchor with unchanged signature/parent must inherit the old Id through the VP-tree layer");

		// listUsers (нетронутый) должен сохранить Id.
		var oldListUsers = first.First(x => x.n == "listUsers");
		var newListUsers = second.First(x => x.n == "listUsers");
		Assert.AreEqual(oldListUsers.id, newListUsers.id,
			"untouched listUsers must keep its Id even when a sibling was renamed");
	}

	// =========================================================================
	// 4b. Слой 2 можно отключить порогом 0 (переменная окружения) — тогда
	// переименованный якорь получает новый Id. Проверяем, что настройка читается.
	// =========================================================================
	[TestMethod]
	public async Task Listing_After_Rename_Does_Not_Stitch_When_Layer2_Disabled()
	{
		Environment.SetEnvironmentVariable("LAND_STITCH_TAU", "0");
		try
		{
			CopyFixtureInto("basic");
			var svc = await CreateInitializedService();
			var first = await ListAnchorsAsync(svc);
			var oldGetUser = first.First(x => x.n == "getUser");

			CopyFixtureInto("rename");
			var second = await ListAnchorsAsync(svc);
			var newAnchor = second.First(x => x.n == "findUserById");

			Assert.AreNotEqual(oldGetUser.id, newAnchor.id, "with tau=0 the fuzzy layer is off and the renamed anchor gets a fresh Id");
		}
		finally
		{
			Environment.SetEnvironmentVariable("LAND_STITCH_TAU", null);
		}
	}

	// =========================================================================
	// 5. Тип-якоря (gqlTypeDef) тоже сшиваются — точечная проверка, что
	// сшивка не привязана к gqlField и работает для recordLike-семейства.
	// =========================================================================
	[TestMethod]
	public async Task Type_Def_Anchors_Are_Stitched_Across_Rescan()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();

		var first = await ListAnchorsAsync(svc);
		var oldUserType = first.FirstOrDefault(x => x.k == "gqlTypeDef" && x.n.EndsWith("User"));
		var oldQueryType = first.FirstOrDefault(x => x.k == "gqlTypeDef" && x.n.EndsWith("Query"));

		Assert.IsNotNull(oldUserType, "basic fixture must yield a gqlTypeDef anchor for User");
		Assert.IsNotNull(oldQueryType, "basic fixture must yield a gqlTypeDef anchor for Query");

		CopyFixtureInto("shifted");
		var second = await ListAnchorsAsync(svc);

		var newUserType = second.FirstOrDefault(x => x.k == "gqlTypeDef" && x.n.EndsWith("User"));
		var newQueryType = second.FirstOrDefault(x => x.k == "gqlTypeDef" && x.n.EndsWith("Query"));

		Assert.IsNotNull(newUserType);
		Assert.IsNotNull(newQueryType);
		Assert.AreEqual(oldUserType.id, newUserType.id, "User type-def Id must survive offset shift");
		Assert.AreEqual(oldQueryType.id, newQueryType.id, "Query type-def Id must survive offset shift");
	}
}
