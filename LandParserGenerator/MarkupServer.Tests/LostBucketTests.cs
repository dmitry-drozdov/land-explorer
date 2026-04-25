using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты _Lost-bucket-а: при сшивке Id "потерянные" auto-якоря, у которых были
/// memberships или links, не отбрасываются — попадают в системную группу _Lost.
/// Пользователь может recover-ить ссылки на новый якорь или discard-нуть полностью.
/// </summary>
[TestClass]
public class LostBucketTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-lost-" + Guid.NewGuid().ToString("N"));
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

	private static TreeNodeClientV2 FindLostBucket(ListTreeResultV2 res)
	{
		foreach (var n in res?.r ?? new List<TreeNodeClientV2>())
		{
			if (string.Equals(n.sys, "lost", StringComparison.OrdinalIgnoreCase))
				return n;
		}
		return null;
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
	public async Task Lost_Bucket_Is_Empty_For_Fresh_Project()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);

		Assert.IsNull(FindLostBucket(listed), "no lost bucket for a clean project");
	}

	[TestMethod]
	public async Task Anchor_With_Membership_Disappears_Goes_To_Lost_Bucket()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		// Положим listUsers в user-группу.
		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Списки" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });

		// removed: listUsers удалён из schema.graphql
		CopyFixtureInto("removed");
		var afterRescan = await ListAsync(svc, forceRescan: true);

		var lostBucket = FindLostBucket(afterRescan);
		Assert.IsNotNull(lostBucket, "_Lost bucket must appear");
		Assert.IsTrue(lostBucket.n.Contains("(1)") || lostBucket.n.Contains("(2)"),
			$"_Lost name should report count, got '{lostBucket.n}'");

		var lostNode = lostBucket.c.SingleOrDefault(x => string.Equals(x.sys, "lostAnchor", StringComparison.OrdinalIgnoreCase) && x.id == lostId);
		Assert.IsNotNull(lostNode, "lost anchor must appear in _Lost bucket");
		StringAssert.Contains(lostNode.n, "listUsers");
		StringAssert.Contains(lostNode.n, "was in:", "lost anchor should report which groups it was in");
		StringAssert.Contains(lostNode.n, "Списки");
	}

	[TestMethod]
	public async Task Anchor_With_Link_Disappears_Goes_To_Lost_Bucket_And_Link_Survives()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var alive = await GetCanonicalAnchorIdByName(svc, "getUser");
		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");

		await svc.AddLinkAsync(new AddLinkParams { sourceId = alive, targetId = lostId, kind = "calls" });

		// listUsers исчезает в коде
		CopyFixtureInto("removed");
		var afterRescan = await ListAsync(svc, forceRescan: true);

		var lostBucket = FindLostBucket(afterRescan);
		Assert.IsNotNull(lostBucket, "_Lost bucket must appear");
		Assert.IsTrue(lostBucket.c.Any(x => x.id == lostId),
			"lost anchor must appear in _Lost bucket because it had a link");

		// Связь сохранилась — peer теперь в lost.
		var fromAlive = await svc.ListLinksAsync(new ListLinksParams { anchorId = alive });
		Assert.AreEqual(1, fromAlive.outgoing.Count, "link must survive in lost-bucket");
		Assert.AreEqual(lostId, fromAlive.outgoing[0].tgt);
		Assert.AreEqual(true, fromAlive.outgoing[0].lost, "peer must be flagged as lost");
	}

	[TestMethod]
	public async Task Anchor_Without_Connections_Is_Dropped_Silently()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		// Никаких memberships, никаких links — listUsers просто исчезает.
		CopyFixtureInto("removed");
		var afterRescan = await ListAsync(svc, forceRescan: true);

		Assert.IsNull(FindLostBucket(afterRescan), "_Lost should not appear when there are no connections");
	}

	[TestMethod]
	public async Task Recover_Lost_Anchor_Moves_Membership_And_Link_To_New_Anchor()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		var anchorAlive = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Списки" })).g;

		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });
		await svc.AddLinkAsync(new AddLinkParams { sourceId = anchorAlive, targetId = lostId });

		CopyFixtureInto("removed");
		await ListAsync(svc, forceRescan: true);

		// Теперь у нас есть lost anchor (listUsers) с membership в "Списки" и одной incoming link.
		// Recover на getUser — после этого listUsers больше нет в _Lost, а membership/link переехал.
		var recover = await svc.RecoverLostAnchorAsync(new RecoverLostAnchorParams
		{
			lostAnchorId = lostId,
			newAnchorId = anchorAlive,
		});
		Assert.AreEqual(true, recover.ok, $"recovery must succeed: {recover.m}");
		Assert.AreEqual(1, recover.recoveredMemberships);
		Assert.AreEqual(1, recover.recoveredLinks);

		var afterRecover = await ListAsync(svc, forceRescan: false);
		Assert.IsNull(FindLostBucket(afterRecover), "_Lost should be empty after recovery");

		// getUser теперь состоит в группе "Списки" (через recovered membership)
		// и больше не имеет никаких links (link был сам-к-себе — отброшен дедупом).
		var listed = await svc.ListLinksAsync(new ListLinksParams { anchorId = anchorAlive });
		Assert.AreEqual(0, listed.outgoing.Count, "self-link after recovery must be dropped by dedup");
	}

	[TestMethod]
	public async Task Recover_Lost_Anchor_Reassigns_Link_To_Different_Live_Anchor()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		var thirdParty = await GetCanonicalAnchorIdByName(svc, "getUser");
		// Резолверы в фикстуре зовутся так же, как gql-поля — getUser/listUsers,
		// поэтому различаем по kind. Берём ts-резолвер getUser (tsMember).
		var resolver = await GetCanonicalAnchorIdByName(svc, "getUser", kind: "tsMember");

		// Связь: getUser -> listUsers (listUsers потеряется)
		await svc.AddLinkAsync(new AddLinkParams { sourceId = thirdParty, targetId = lostId, kind = "calls" });

		CopyFixtureInto("removed");
		await ListAsync(svc, forceRescan: true);

		// Переадресуем lost listUsers на резолвер getUserResolver — типа "ой, это был не listUsers, а его резолвер".
		var recover = await svc.RecoverLostAnchorAsync(new RecoverLostAnchorParams
		{
			lostAnchorId = lostId,
			newAnchorId = resolver,
		});
		Assert.AreEqual(true, recover.ok);
		Assert.AreEqual(1, recover.recoveredLinks);

		// Теперь link getUser -> getUserResolver
		var fromThird = await svc.ListLinksAsync(new ListLinksParams { anchorId = thirdParty });
		Assert.AreEqual(1, fromThird.outgoing.Count);
		Assert.AreEqual(resolver, fromThird.outgoing[0].tgt);
	}

	[TestMethod]
	public async Task Discard_Lost_Anchor_Removes_It_And_Its_Memberships_And_Links()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		var anchorAlive = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;

		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });
		await svc.AddLinkAsync(new AddLinkParams { sourceId = anchorAlive, targetId = lostId });

		CopyFixtureInto("removed");
		await ListAsync(svc, forceRescan: true);

		var discard = await svc.DiscardLostAnchorAsync(new DiscardLostAnchorParams { lostAnchorId = lostId });
		Assert.AreEqual(true, discard.ok);

		var after = await ListAsync(svc, forceRescan: false);
		Assert.IsNull(FindLostBucket(after), "_Lost should be empty after discard");

		var fromAlive = await svc.ListLinksAsync(new ListLinksParams { anchorId = anchorAlive });
		Assert.AreEqual(0, fromAlive.outgoing.Count, "link to discarded lost anchor must be removed");
	}

	[TestMethod]
	public async Task Lost_Anchor_Survives_Multiple_Rescans_Until_Recovered_Or_Discarded()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });

		CopyFixtureInto("removed");
		await ListAsync(svc, forceRescan: true);
		await ListAsync(svc, forceRescan: true); // повторный rescan не должен потерять lost
		var third = await ListAsync(svc, forceRescan: true);

		var bucket = FindLostBucket(third);
		Assert.IsNotNull(bucket, "_Lost bucket must persist across multiple rescans");
		Assert.IsTrue(bucket.c.Any(x => x.id == lostId), "lost anchor must still be present");
	}
}
