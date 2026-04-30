using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты пользовательского комментария (Comment).
/// Поле живёт по стабильному AnchorId / UserGroup.Id, переживает rebind,
/// forceRescan и попадание в _Lost-bucket.
/// </summary>
[TestClass]
public class CommentTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-comment-" + Guid.NewGuid().ToString("N"));
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
	public async Task Set_Comment_On_Anchor_Persists_And_Returns_In_DTO()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");

		var res = await svc.SetCommentAsync(new SetCommentParams { targetId = anchorId, comment = "проверяет права" });
		Assert.AreEqual(true, res.ok);

		var listed = await ListAsync(svc, forceRescan: false);
		var anchor = AllAnchors(listed).Single(x => x.id == anchorId && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual("проверяет права", anchor.cm);
	}

	[TestMethod]
	public async Task Set_Comment_On_User_Group_Persists()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Auth" })).g;
		var res = await svc.SetCommentAsync(new SetCommentParams { targetId = grp.id, comment = "точки авторизации" });
		Assert.AreEqual(true, res.ok);

		var listed = await ListAsync(svc, forceRescan: false);
		var groupNode = listed.r.Single(n => n.id == grp.id);
		Assert.AreEqual("точки авторизации", groupNode.cm);
	}

	[TestMethod]
	public async Task Empty_Comment_Removes_Existing()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		await svc.SetCommentAsync(new SetCommentParams { targetId = anchorId, comment = "x" });
		await svc.SetCommentAsync(new SetCommentParams { targetId = anchorId, comment = "" });

		var listed = await ListAsync(svc, forceRescan: false);
		var anchor = AllAnchors(listed).Single(x => x.id == anchorId && string.IsNullOrEmpty(x.aid));
		Assert.IsTrue(string.IsNullOrEmpty(anchor.cm), "empty comment should clear");
	}

	[TestMethod]
	public async Task Comment_Survives_ForceRescan_Via_Stitching()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		await svc.SetCommentAsync(new SetCommentParams { targetId = anchorId, comment = "stays" });

		// shifted: те же якоря, новые offsets — Layer 1 сшивки сохранит Id и Comment.
		CopyFixtureInto("shifted");
		var afterRescan = await ListAsync(svc, forceRescan: true);

		var anchor = AllAnchors(afterRescan).Single(x => x.id == anchorId && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual("stays", anchor.cm, "comment must survive forceRescan");
	}

	[TestMethod]
	public async Task Comment_Survives_UpdateAnchor_Rebind()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		await svc.SetCommentAsync(new SetCommentParams { targetId = anchorId, comment = "after-rebind" });

		// Сменим фикстуру → офсеты сместятся → updateAnchor подвинет якорь
		// (через VP-tree) и сохранит Id, IsManual и Comment.
		CopyFixtureInto("shifted");
		await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = anchorId });

		var listed = await ListAsync(svc, forceRescan: false);
		var anchor = AllAnchors(listed).Single(x => x.id == anchorId && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual("after-rebind", anchor.cm);
	}

	[TestMethod]
	public async Task User_Group_Comment_Survives_ForceRescan()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Бизнес" })).g;
		await svc.SetCommentAsync(new SetCommentParams { targetId = grp.id, comment = "ключевая логика" });

		CopyFixtureInto("shifted");
		var listed = await ListAsync(svc, forceRescan: true);
		var groupNode = listed.r.Single(n => n.id == grp.id);
		Assert.AreEqual("ключевая логика", groupNode.cm);
	}

	[TestMethod]
	public async Task Lost_Anchor_Carries_Its_Comment()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		await svc.SetCommentAsync(new SetCommentParams { targetId = lostId, comment = "будет утерян" });

		// Дадим ему membership чтобы попал в _Lost (а не молчаливо отбросился).
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Z" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });

		CopyFixtureInto("removed");
		var afterRescan = await ListAsync(svc, forceRescan: true);

		var lostBucket = afterRescan.r.FirstOrDefault(n => string.Equals(n.sys, "lost", StringComparison.OrdinalIgnoreCase));
		Assert.IsNotNull(lostBucket);
		var lostNode = lostBucket.c.Single(x => x.id == lostId);
		Assert.AreEqual("будет утерян", lostNode.cm, "lost anchor must keep its comment for later recovery");
	}

	[TestMethod]
	public async Task Recover_Lost_Anchor_Transfers_Comment_If_New_Anchor_Has_None()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		await svc.SetCommentAsync(new SetCommentParams { targetId = lostId, comment = "переедет на новый" });

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "G" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });

		CopyFixtureInto("removed");
		await ListAsync(svc, forceRescan: true);

		var newAnchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		await svc.RecoverLostAnchorAsync(new RecoverLostAnchorParams { lostAnchorId = lostId, newAnchorId = newAnchorId });

		var listed = await ListAsync(svc, forceRescan: false);
		var newAnchor = AllAnchors(listed).Single(x => x.id == newAnchorId && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual("переедет на новый", newAnchor.cm, "recover should move comment when new has none");
	}

	[TestMethod]
	public async Task Recover_Lost_Anchor_Keeps_New_Anchor_Comment_Untouched_If_Set()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var lostId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		await svc.SetCommentAsync(new SetCommentParams { targetId = lostId, comment = "из лоста" });

		var newAnchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		await svc.SetCommentAsync(new SetCommentParams { targetId = newAnchorId, comment = "уже свой" });

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "H" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = lostId, groupId = grp.id });

		CopyFixtureInto("removed");
		await ListAsync(svc, forceRescan: true);

		await svc.RecoverLostAnchorAsync(new RecoverLostAnchorParams { lostAnchorId = lostId, newAnchorId = newAnchorId });

		var listed = await ListAsync(svc, forceRescan: false);
		var newAnchor = AllAnchors(listed).Single(x => x.id == newAnchorId && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual("уже свой", newAnchor.cm, "existing comment on new anchor must not be overwritten");
	}
}
