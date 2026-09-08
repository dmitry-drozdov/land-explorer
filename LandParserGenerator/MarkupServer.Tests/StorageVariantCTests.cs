using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;
using Newtonsoft.Json.Linq;

namespace MarkupServer.Tests;

/// <summary>
/// «Вариант C» хранения (markup/experiments/e13_storage_size, §2б):
///  • .land/anchors.json (версия 10) не содержит мешков соседей; при загрузке они строятся заново
///    и совпадают с мешками, посчитанными при извлечении;
///  • снимок VP-дерева каждого профиля кэшируется в .land/vp_index.json; при неизменном входе дерево
///    восстанавливается из кэша и даёт те же k-NN, что построенное; при изменении входа строится заново.
/// </summary>
[TestClass]
public class StorageVariantCTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");
	private const string GqlFieldProfile = "gql:callableMember:gqlField";

	private string _tempFolder;

	public TestContext TestContext { get; set; }

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-storage-" + Guid.NewGuid().ToString("N"));
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

	private async Task RescanAsync(LandService svc)
	{
		await svc.ListAnchorsAsync(new ListTreeParams { folderPath = _tempFolder, forceRescan = true, preferCache = false });
	}

	private static Dictionary<string, TreeNode> FieldAnchorsById(PersistedMarkup m)
		=> (m?.Anchors ?? new List<TreeNode>())
			.Where(a => a != null && string.Equals(a.AnchorKind, "gqlField", StringComparison.OrdinalIgnoreCase))
			.ToDictionary(a => a.Id, StringComparer.Ordinal);

	[TestMethod]
	public async Task Store_V10_HasNoNeighborBags_And_LoadRebuildsIdenticalOnes()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await RescanAsync(svc);

		var path = AnchorStore.GetStorePath(_tempFolder);
		Assert.IsTrue(File.Exists(path), "рескан должен записать .land/anchors.json");

		var text = File.ReadAllText(path);
		Assert.IsFalse(text.Contains("\"NeighborBag\"", StringComparison.Ordinal), "в файле версии 10 мешков соседей быть не должно");
		Assert.AreEqual(AnchorStore.CurrentVersion, (int)JObject.Parse(text)["Version"]);

		Assert.IsTrue(AnchorStore.TryLoad(_tempFolder, out var loaded, out var err), err ?? "");
		Assert.IsTrue(string.IsNullOrEmpty(err), "загрузка не должна сообщать об ошибках: " + err);

		var inMemory = FieldAnchorsById(svc.CurrentMarkupForTests);
		var fromDisk = FieldAnchorsById(loaded);
		Assert.IsTrue(inMemory.Count > 1, "в fixture должны быть поля GraphQL");
		Assert.AreEqual(inMemory.Count, fromDisk.Count);

		foreach (var (id, disk) in fromDisk)
		{
			var mem = inMemory[id];
			if (mem.NeighborBag == null)
			{
				Assert.IsNull(disk.NeighborBag, $"{id}: у якоря без соседей мешка быть не должно");
				continue;
			}
			Assert.IsNotNull(disk.NeighborBag, $"{id}: мешок не восстановлен после загрузки");
			CollectionAssert.AreEquivalent(mem.NeighborBag.Keys.ToList(), disk.NeighborBag.Keys.ToList(), $"{id}: ключи мешка");
			foreach (var kv in mem.NeighborBag)
				Assert.AreEqual(kv.Value, disk.NeighborBag[kv.Key], $"{id}: вес {kv.Key}");
		}
		Assert.IsTrue(inMemory.Values.Any(a => a.NeighborBag != null && a.NeighborBag.Count > 0), "хотя бы у одного поля должен быть мешок");
	}

	[TestMethod]
	public async Task TreeCache_RestoresIdenticalForest_And_InvalidatesOnChange()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await RescanAsync(svc);

		svc.BuildCurrentRebindingForestsFromDisk();
		Assert.IsTrue(svc.LastForestSource.Count > 0, "должен быть хотя бы один профиль");
		Assert.IsTrue(svc.LastForestSource.Values.All(x => x == "built"), "первый раз деревья строятся");
		Assert.IsTrue(File.Exists(TreeCacheStore.GetPath(_tempFolder)), "кэш снимков должен быть записан");

		svc.BuildCurrentRebindingForestsFromDisk();
		Assert.IsTrue(svc.LastForestSource.ContainsKey(GqlFieldProfile), "профиль полей GraphQL");
		Assert.IsTrue(svc.LastForestSource.Values.All(x => x == "cache"), "при неизменном входе деревья берутся из кэша: "
			+ string.Join(", ", svc.LastForestSource.Select(kv => kv.Key + "=" + kv.Value)));
		Assert.AreEqual(0, svc.CountForestMismatchesAgainstFreshBuild(), "восстановленные деревья должны давать те же k-NN");

		CopyFixtureInto("rename");
		svc.BuildCurrentRebindingForestsFromDisk();
		Assert.AreEqual("built", svc.LastForestSource[GqlFieldProfile], "после изменения схемы хэш другой — дерево строится заново");
		Assert.AreEqual(0, svc.CountForestMismatchesAgainstFreshBuild());
	}

	[TestMethod]
	public async Task TreeCache_Disabled_ByEnvironment_AlwaysBuilds()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await RescanAsync(svc);

		var prev = TreeCacheStore.Enabled;
		TreeCacheStore.Enabled = false;
		try
		{
			svc.BuildCurrentRebindingForestsFromDisk();
			svc.BuildCurrentRebindingForestsFromDisk();
			Assert.IsTrue(svc.LastForestSource.Values.All(x => x == "built"));
			Assert.IsFalse(File.Exists(TreeCacheStore.GetPath(_tempFolder)));
		}
		finally
		{
			TreeCacheStore.Enabled = prev;
		}
	}

	/// <summary>
	/// Локальное измерение (пропускается, если файла нет): пересохранение реального .land/anchors.json
	/// старой версии без мешков. Числа — для markup/experiments/e13_storage_size/REPORT.md §2б.
	/// </summary>
	[TestMethod]
	[TestCategory("Local")]
	public void LocalStore_Resave_WithoutBags_Shrinks()
	{
		var candidates = new[]
		{
			@"E:\phd\my\graphql_ts_2600_gqlfields_project\.land\anchors.json",
			@"E:\phd\my\graphql_ts_100_gqlfields_project\.land\anchors.json",
			@"E:\phd\my\for_plugin\markup\test-project\.land\anchors.json",
		};
		var found = candidates.Where(File.Exists).ToList();
		if (found.Count == 0)
			Assert.Inconclusive("локальных файлов .land/anchors.json нет");

		foreach (var src in found)
		{
			var folder = Path.Combine(_tempFolder, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(src))!));
			Directory.CreateDirectory(Path.Combine(folder, ".land"));
			File.Copy(src, AnchorStore.GetStorePath(folder), true);
			var before = new FileInfo(AnchorStore.GetStorePath(folder)).Length;

			Assert.IsTrue(AnchorStore.TryLoad(folder, out var data, out var err), err ?? "");
			Assert.IsTrue(AnchorStore.TrySave(folder, data, out err), err ?? "");
			var after = new FileInfo(AnchorStore.GetStorePath(folder)).Length;
			var withBags = data.Anchors.Count(a => a.NeighborBag != null && a.NeighborBag.Count > 0);
			// сколько занял бы файл, если бы текущий сервер писал мешки (как версия 9): те же данные, сериализатор по умолчанию
			var ifBagsWritten = System.Text.Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.SerializeObject(
				data, Newtonsoft.Json.Formatting.Indented, new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }));

			TestContext.WriteLine($"{src}: anchors={data.Anchors.Count} (with bags in memory {withBags}), bytes before={before} after={after} ratio={(double)before / Math.Max(1, after):F2}; "
				+ $"if current bags were written={ifBagsWritten} ratio={(double)ifBagsWritten / Math.Max(1, after):F2}");
			Assert.IsTrue(after < before, $"{src}: файл без мешков должен быть меньше");
			Assert.IsFalse(File.ReadAllText(AnchorStore.GetStorePath(folder)).Contains("\"NeighborBag\"", StringComparison.Ordinal));
		}
	}
}
