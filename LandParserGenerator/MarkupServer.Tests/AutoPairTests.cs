using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты auto-pair: gqlField↔ts-резолвер автоматически помечаются маркером pr=true,
/// синтезируются в Links с системным kind="_autoPair", фильтруются из бейджей и
/// listLinks, не сохраняются на диск.
/// </summary>
[TestClass]
public class AutoPairTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-pair-" + Guid.NewGuid().ToString("N"));
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
		catch { /* best effort */ }
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

	private async Task<ListTreeResultV2> ListAsync(LandService svc, bool forceRescan = true)
	{
		return await svc.ListAnchorsAsync(new ListTreeParams
		{
			folderPath = _tempFolder,
			forceRescan = forceRescan,
			preferCache = !forceRescan,
		});
	}

	private static List<TreeNodeClientV2> AllAnchors(ListTreeResultV2 res)
	{
		var sink = new List<TreeNodeClientV2>();
		Walk(res?.r, n =>
		{
			if (string.Equals(n.t, "anchor", StringComparison.OrdinalIgnoreCase))
				sink.Add(n);
		});
		return sink;
	}

	private static void Walk(List<TreeNodeClientV2> nodes, Action<TreeNodeClientV2> visit)
	{
		if (nodes == null) return;
		foreach (var n in nodes)
		{
			if (n == null) continue;
			visit(n);
			if (n.c != null) Walk(n.c, visit);
		}
	}

	private async Task<string> GetCanonicalAnchorIdByName(LandService svc, string anchorName, string kind = "gqlField")
	{
		var res = await ListAsync(svc, forceRescan: false);
		var match = AllAnchors(res).FirstOrDefault(x => x.n == anchorName && x.k == kind && string.IsNullOrEmpty(x.aid));
		Assert.IsNotNull(match, $"canonical anchor '{anchorName}' (kind={kind}) not found");
		return match.id;
	}

	// =========================================================================

	[TestMethod]
	public async Task GqlField_With_Resolver_Has_Paired_Marker_On_Both_Ends()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);

		// Все canonical-анкоры с именем getUser — gql и ts.
		var canon = AllAnchors(listed).Where(x => x.n == "getUser" && string.IsNullOrEmpty(x.aid)).ToList();
		Assert.AreEqual(2, canon.Count, "fixture must produce gql and ts canonical 'getUser'");

		var gql = canon.Single(x => x.k == "gqlField");
		var ts = canon.Single(x => x.k == "tsMember");
		Assert.AreEqual(true, gql.pr, "gqlField getUser must be paired");
		Assert.AreEqual(true, ts.pr, "ts-резолвер getUser must be paired");
	}

	[TestMethod]
	public async Task GqlType_Anchor_Without_Resolver_Is_Not_Paired()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);

		// Тип-якоря (gqlTypeDef) не входят в Relations — не должны быть paired.
		var typeAnchors = AllAnchors(listed).Where(x => x.k == "gqlTypeDef" && string.IsNullOrEmpty(x.aid)).ToList();
		Assert.IsTrue(typeAnchors.Count > 0, "fixture must produce some gqlTypeDef anchors");
		foreach (var t in typeAnchors)
		{
			Assert.IsTrue(t.pr == null || t.pr == false, $"type anchor '{t.n}' should not be paired");
		}
	}

	[TestMethod]
	public async Task Auto_Pair_Is_Not_Counted_In_OutCount_Or_InCount()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		// Никаких user-связей не создаём — только auto-pair от Relations.
		var listed = await ListAsync(svc, forceRescan: false);
		var gql = AllAnchors(listed).Single(x => x.n == "getUser" && x.k == "gqlField" && string.IsNullOrEmpty(x.aid));
		var ts = AllAnchors(listed).Single(x => x.n == "getUser" && x.k == "tsMember" && string.IsNullOrEmpty(x.aid));

		Assert.IsTrue(gql.oc == null || gql.oc == 0, "auto-pair must NOT contribute to oc");
		Assert.IsTrue(gql.ic == null || gql.ic == 0, "auto-pair must NOT contribute to ic");
		Assert.IsTrue(ts.oc == null || ts.oc == 0, "auto-pair must NOT contribute to oc on ts side");
		Assert.IsTrue(ts.ic == null || ts.ic == 0, "auto-pair must NOT contribute to ic on ts side");
	}

	[TestMethod]
	public async Task Auto_Pair_Is_Not_Returned_From_ListLinks()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var gqlId = await GetCanonicalAnchorIdByName(svc, "getUser", "gqlField");

		var links = await svc.ListLinksAsync(new ListLinksParams { anchorId = gqlId });
		Assert.AreEqual(0, links.outgoing.Count, "auto-pair must be hidden from listLinks");
		Assert.AreEqual(0, links.incoming.Count, "no incoming links expected");
	}

	[TestMethod]
	public async Task User_Link_And_Auto_Pair_Coexist_Without_Mixing()
	{
		// User-Link виден в счётчиках/listLinks, auto-pair только через pr.
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var gqlGetUser = await GetCanonicalAnchorIdByName(svc, "getUser", "gqlField");
		var gqlListUsers = await GetCanonicalAnchorIdByName(svc, "listUsers", "gqlField");

		await svc.AddLinkAsync(new AddLinkParams { sourceId = gqlGetUser, targetId = gqlListUsers, kind = "calls" });

		var listed = await ListAsync(svc, forceRescan: false);
		var gql = AllAnchors(listed).Single(x => x.id == gqlGetUser);

		Assert.AreEqual(true, gql.pr, "auto-pair marker must remain even when user-link exists");
		Assert.AreEqual(1, gql.oc, "user-link must show in oc");

		var links = await svc.ListLinksAsync(new ListLinksParams { anchorId = gqlGetUser });
		Assert.AreEqual(1, links.outgoing.Count, "listLinks shows only the user-link, not auto-pair");
		Assert.AreEqual("calls", links.outgoing[0].kind);
	}

	[TestMethod]
	public async Task AddLink_Rejects_Reserved_Auto_Pair_Kind()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var a = await GetCanonicalAnchorIdByName(svc, "getUser", "gqlField");
		var b = await GetCanonicalAnchorIdByName(svc, "listUsers", "gqlField");

		var res = await svc.AddLinkAsync(new AddLinkParams { sourceId = a, targetId = b, kind = "_autoPair" });
		Assert.IsNull(res.l, "addLink with reserved kind must be rejected");
		StringAssert.Contains(res.m ?? "", "зарезервирован");
	}

	[TestMethod]
	public async Task Auto_Pair_Links_Are_Not_Persisted_To_Disk()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var snapshotPath = Path.Combine(_tempFolder, ".land", "anchors.json");
		Assert.IsTrue(File.Exists(snapshotPath));

		var json = File.ReadAllText(snapshotPath);
		Assert.IsFalse(json.Contains("_autoPair"), "auto-pair must NOT appear in persisted snapshot");
	}

	[TestMethod]
	public async Task Auto_Pair_Survives_ForceRescan_When_Resolver_Stays()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		// shifted: те же якоря, новые offsets — Relations и пара пересоберутся.
		CopyFixtureInto("shifted");
		var listed = await ListAsync(svc, forceRescan: true);

		var gql = AllAnchors(listed).Single(x => x.n == "getUser" && x.k == "gqlField" && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual(true, gql.pr, "auto-pair must survive forceRescan when relation persists");
	}

	[TestMethod]
	public async Task Shadow_In_User_Group_Does_Not_Show_Paired_Marker()
	{
		// Свойство paired относится к auto-структуре (ядро gqlField-группы),
		// а в user-папке shadow — это просто ссылка. ◆ туда не едет.
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser", "gqlField");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "FavPair" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });

		var listed = await ListAsync(svc, forceRescan: false);
		var shadow = AllAnchors(listed).FirstOrDefault(x => !string.IsNullOrEmpty(x.aid) && x.aid == anchorId);
		Assert.IsNotNull(shadow, "shadow must exist in user group");
		Assert.IsTrue(shadow.pr == null || shadow.pr == false, "shadow must NOT carry the paired marker");

		// Canonical в auto-структуре всё ещё с маркером.
		var canonical = AllAnchors(listed).FirstOrDefault(x => x.id == anchorId && string.IsNullOrEmpty(x.aid));
		Assert.IsNotNull(canonical);
		Assert.AreEqual(true, canonical.pr, "canonical still carries the paired marker");
	}
}
