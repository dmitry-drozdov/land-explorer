using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты кросс-якорных связей (этап 2): создание/удаление/листинг,
/// каскад при удалении якоря, переживаемость через rebind и forceRescan.
/// </summary>
[TestClass]
public class LinkTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-links-" + Guid.NewGuid().ToString("N"));
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
	public async Task Add_Link_Creates_Outgoing_And_Incoming_Records()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var src = await GetCanonicalAnchorIdByName(svc, "getUser");
		var tgt = await GetCanonicalAnchorIdByName(svc, "listUsers");

		var add = await svc.AddLinkAsync(new AddLinkParams
		{
			sourceId = src,
			targetId = tgt,
			kind = "calls",
			label = "fan-out",
		});

		Assert.IsNotNull(add?.l);
		StringAssert.StartsWith(add.l.id, "link:");
		Assert.AreEqual(src, add.l.src);
		Assert.AreEqual(tgt, add.l.tgt);

		var fromSrc = await svc.ListLinksAsync(new ListLinksParams { anchorId = src });
		Assert.AreEqual(1, fromSrc.outgoing.Count);
		Assert.AreEqual(0, fromSrc.incoming.Count);
		Assert.AreEqual("calls", fromSrc.outgoing[0].kind);
		Assert.AreEqual("fan-out", fromSrc.outgoing[0].label);
		Assert.AreEqual("listUsers", fromSrc.outgoing[0].pn, "outgoing peer name should be target's name");

		var fromTgt = await svc.ListLinksAsync(new ListLinksParams { anchorId = tgt });
		Assert.AreEqual(0, fromTgt.outgoing.Count);
		Assert.AreEqual(1, fromTgt.incoming.Count);
		Assert.AreEqual("getUser", fromTgt.incoming[0].pn, "incoming peer name should be source's name");
	}

	[TestMethod]
	public async Task Add_Link_Self_Reference_Is_Rejected()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var a = await GetCanonicalAnchorIdByName(svc, "getUser");
		var add = await svc.AddLinkAsync(new AddLinkParams { sourceId = a, targetId = a });
		Assert.IsNull(add.l, "self-link must not be created");
	}

	[TestMethod]
	public async Task Add_Link_Duplicate_With_Same_Kind_Is_Idempotent()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var src = await GetCanonicalAnchorIdByName(svc, "getUser");
		var tgt = await GetCanonicalAnchorIdByName(svc, "listUsers");

		var first = await svc.AddLinkAsync(new AddLinkParams { sourceId = src, targetId = tgt, kind = "calls" });
		var second = await svc.AddLinkAsync(new AddLinkParams { sourceId = src, targetId = tgt, kind = "calls" });

		Assert.AreEqual(first.l.id, second.l.id, "duplicate add returns the same link");

		var listed = await svc.ListLinksAsync(new ListLinksParams { anchorId = src });
		Assert.AreEqual(1, listed.outgoing.Count);
	}

	[TestMethod]
	public async Task Remove_Link_Drops_It_From_Both_Sides()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var src = await GetCanonicalAnchorIdByName(svc, "getUser");
		var tgt = await GetCanonicalAnchorIdByName(svc, "listUsers");

		var add = await svc.AddLinkAsync(new AddLinkParams { sourceId = src, targetId = tgt });
		var rm = await svc.RemoveLinkAsync(new RemoveLinkParams { linkId = add.l.id });
		Assert.AreEqual(true, rm.ok);

		var fromSrc = await svc.ListLinksAsync(new ListLinksParams { anchorId = src });
		var fromTgt = await svc.ListLinksAsync(new ListLinksParams { anchorId = tgt });
		Assert.AreEqual(0, fromSrc.outgoing.Count);
		Assert.AreEqual(0, fromTgt.incoming.Count);
	}

	[TestMethod]
	public async Task Delete_Anchor_Cascades_All_Links_Where_It_Appears()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var a = await GetCanonicalAnchorIdByName(svc, "getUser");
		var b = await GetCanonicalAnchorIdByName(svc, "listUsers");
		// В фикстуре resolvers.ts резолверы зовутся как gql-поля — getUser/listUsers,
		// поэтому различаем по kind="tsMember".
		var c = await GetCanonicalAnchorIdByName(svc, "getUser", kind: "tsMember");

		await svc.AddLinkAsync(new AddLinkParams { sourceId = a, targetId = b });
		await svc.AddLinkAsync(new AddLinkParams { sourceId = c, targetId = a });

		await svc.DeleteAnchorAsync(new DeleteAnchorParams { anchorId = a });

		var fromB = await svc.ListLinksAsync(new ListLinksParams { anchorId = b });
		var fromC = await svc.ListLinksAsync(new ListLinksParams { anchorId = c });

		Assert.AreEqual(0, fromB.incoming.Count, "incoming link from a -> b must be cascaded");
		Assert.AreEqual(0, fromC.outgoing.Count, "outgoing link from c -> a must be cascaded");
	}

	[TestMethod]
	public async Task Anchor_Badge_Counters_OutCount_And_InCount_Are_Set()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var a = await GetCanonicalAnchorIdByName(svc, "getUser");
		var b = await GetCanonicalAnchorIdByName(svc, "listUsers");

		await svc.AddLinkAsync(new AddLinkParams { sourceId = a, targetId = b, kind = "calls" });

		var listed = await ListAsync(svc, forceRescan: false);
		var canonicalA = AllAnchors(listed).Single(x => x.id == a && string.IsNullOrEmpty(x.aid));
		var canonicalB = AllAnchors(listed).Single(x => x.id == b && string.IsNullOrEmpty(x.aid));

		Assert.AreEqual(1, canonicalA.oc, "source anchor must have outCount=1");
		Assert.IsTrue(canonicalA.ic == null || canonicalA.ic == 0);
		Assert.AreEqual(1, canonicalB.ic, "target anchor must have inCount=1");
		Assert.IsTrue(canonicalB.oc == null || canonicalB.oc == 0);
	}

	[TestMethod]
	public async Task Links_Survive_ForceRescan_When_Anchors_Stitched()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var a = await GetCanonicalAnchorIdByName(svc, "getUser");
		var b = await GetCanonicalAnchorIdByName(svc, "listUsers");

		await svc.AddLinkAsync(new AddLinkParams { sourceId = a, targetId = b, kind = "calls" });

		// shifted: те же якоря, но offsets сдвинуты — слой 1 сшивки сохранит Id
		CopyFixtureInto("shifted");
		var afterRescan = await ListAsync(svc, forceRescan: true);

		// Anchors всё ещё на месте — Id сохранены
		var anchors = AllAnchors(afterRescan).Where(x => string.IsNullOrEmpty(x.aid)).ToList();
		Assert.IsTrue(anchors.Any(x => x.id == a), "anchor a Id must survive forceRescan");
		Assert.IsTrue(anchors.Any(x => x.id == b), "anchor b Id must survive forceRescan");

		var fromA = await svc.ListLinksAsync(new ListLinksParams { anchorId = a });
		Assert.AreEqual(1, fromA.outgoing.Count, "link must survive forceRescan because both endpoints stitched");
		Assert.AreEqual(b, fromA.outgoing[0].tgt);
	}
}
