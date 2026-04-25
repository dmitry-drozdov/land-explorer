using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты пользовательских групп и multi-membership (этап 1).
///
/// Сценарии: создание группы, добавление одного якоря в несколько групп,
/// идемпотентность add, корректность shadow-узлов, удаление членства,
/// удаление группы, каскадное удаление memberships при удалении якоря,
/// сохранение групп между ресканами и подчистка висячих memberships.
/// </summary>
[TestClass]
public class UserGroupTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-ug-" + Guid.NewGuid().ToString("N"));
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

	private static List<TreeNodeClientV2> RootGroupsByKind(ListTreeResultV2 res, string kindOrNull)
	{
		// kindOrNull == "user" -> только user-группы; null -> только auto-группы.
		var out_ = new List<TreeNodeClientV2>();
		foreach (var n in res?.r ?? new List<TreeNodeClientV2>())
		{
			if (n == null || !string.Equals(n.t, "group", StringComparison.OrdinalIgnoreCase)) continue;
			var isUser = string.Equals(n.gk, "user", StringComparison.OrdinalIgnoreCase);
			if (kindOrNull == "user" ? isUser : !isUser)
				out_.Add(n);
		}
		return out_;
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
		var anchors = AllAnchors(res);
		var match = anchors.FirstOrDefault(x => x.n == anchorName && x.k == kind && string.IsNullOrEmpty(x.aid));
		Assert.IsNotNull(match, $"canonical anchor '{anchorName}' (kind={kind}) not found in tree");
		return match.id;
	}

	// =========================================================================

	[TestMethod]
	public async Task Create_User_Group_Appears_In_Tree_Above_Auto_Groups()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc); // первичный snapshot

		var created = await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Авторизация" });
		Assert.IsNotNull(created?.g, "createUserGroup must return the new group");
		Assert.AreEqual("user", created.g.gk);
		Assert.AreEqual("Авторизация", created.g.n);
		StringAssert.StartsWith(created.g.id, "userGroup:");

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroups = RootGroupsByKind(listed, "user");
		var autoGroups = RootGroupsByKind(listed, null);

		Assert.AreEqual(1, userGroups.Count);
		Assert.AreEqual(created.g.id, userGroups[0].id);

		// User-группа должна стоять перед auto-группами в Roots.
		Assert.IsTrue(autoGroups.Count > 0, "basic fixture must produce some auto-groups");
		var rootIds = listed.r.Select(n => n.id).ToList();
		var firstUserIdx = rootIds.IndexOf(created.g.id);
		var firstAutoIdx = rootIds.IndexOf(autoGroups[0].id);
		Assert.IsTrue(firstUserIdx < firstAutoIdx, "user-groups must precede auto-groups in tree roots");
	}

	[TestMethod]
	public async Task Add_Anchor_To_Group_Renders_Shadow_Node_With_Aid()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка 1" })).g;

		var addRes = await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams
		{
			anchorId = anchorId,
			groupId = grp.id,
		});
		Assert.AreEqual(true, addRes.ok);
		Assert.AreEqual(false, addRes.alreadyMember);

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroup = RootGroupsByKind(listed, "user").Single();

		Assert.IsNotNull(userGroup.c, "user group must have children after addAnchor");
		Assert.AreEqual(1, userGroup.c.Count);
		var shadow = userGroup.c[0];
		Assert.AreEqual("anchor", shadow.t);
		Assert.AreEqual(anchorId, shadow.aid, "shadow.aid must point at the canonical anchor");
		Assert.AreNotEqual(anchorId, shadow.id, "shadow.id must be unique (memberOf:...) and differ from canonical id");
		StringAssert.StartsWith(shadow.id, "memberOf:");

		// canonical всё ещё на месте в auto-дереве
		var canonicalCount = AllAnchors(listed).Count(x => x.id == anchorId && string.IsNullOrEmpty(x.aid));
		Assert.AreEqual(1, canonicalCount);
	}

	[TestMethod]
	public async Task Same_Anchor_In_Multiple_Groups_Yields_Distinct_Shadows()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var g1 = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка A" })).g;
		var g2 = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка B" })).g;

		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = g1.id });
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = g2.id });

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroups = RootGroupsByKind(listed, "user");
		Assert.AreEqual(2, userGroups.Count);

		var shadow1 = userGroups.First(x => x.id == g1.id).c.Single();
		var shadow2 = userGroups.First(x => x.id == g2.id).c.Single();

		Assert.AreEqual(anchorId, shadow1.aid);
		Assert.AreEqual(anchorId, shadow2.aid);
		Assert.AreNotEqual(shadow1.id, shadow2.id, "shadow ids must differ across groups");
	}

	[TestMethod]
	public async Task Add_Anchor_Twice_Is_Idempotent_Reports_AlreadyMember()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;

		var first = await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });
		var second = await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });

		Assert.AreEqual(false, first.alreadyMember);
		Assert.AreEqual(true, second.alreadyMember);

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroup = RootGroupsByKind(listed, "user").Single();
		Assert.AreEqual(1, userGroup.c.Count, "duplicate add must not produce a second shadow");
	}

	[TestMethod]
	public async Task Remove_Anchor_From_Group_Drops_Only_The_Shadow()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;

		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });
		var removeRes = await svc.RemoveAnchorFromGroupAsync(new RemoveAnchorFromGroupParams { anchorId = anchorId, groupId = grp.id });
		Assert.AreEqual(true, removeRes.ok);

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroup = RootGroupsByKind(listed, "user").Single();
		Assert.AreEqual(0, userGroup.c?.Count ?? 0, "shadow must disappear after removeAnchorFromUserGroup");

		// canonical anchor живой
		Assert.AreEqual(1, AllAnchors(listed).Count(x => x.id == anchorId && string.IsNullOrEmpty(x.aid)));
	}

	[TestMethod]
	public async Task Rename_User_Group_Updates_Tree()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Old" })).g;
		var renamed = await svc.RenameUserGroupAsync(new RenameUserGroupParams { groupId = grp.id, name = "New" });
		Assert.AreEqual("New", renamed.g.n);

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroup = RootGroupsByKind(listed, "user").Single();
		Assert.AreEqual("New", userGroup.n);
		Assert.AreEqual(grp.id, userGroup.id);
	}

	[TestMethod]
	public async Task Delete_User_Group_Cascades_Memberships_Without_Touching_Anchors()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });

		var del = await svc.DeleteUserGroupAsync(new DeleteUserGroupParams { groupId = grp.id });
		Assert.AreEqual(true, del.ok);
		Assert.AreEqual(1, del.removedMemberships);

		var listed = await ListAsync(svc, forceRescan: false);
		Assert.AreEqual(0, RootGroupsByKind(listed, "user").Count);

		// canonical anchor живой
		Assert.AreEqual(1, AllAnchors(listed).Count(x => x.id == anchorId && string.IsNullOrEmpty(x.aid)));
	}

	[TestMethod]
	public async Task Delete_Anchor_Cascades_Membership()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });

		var delAnchor = await svc.DeleteAnchorAsync(new DeleteAnchorParams { anchorId = anchorId });
		Assert.AreEqual(true, delAnchor.d);

		var listed = await ListAsync(svc, forceRescan: false);
		var userGroup = RootGroupsByKind(listed, "user").Single();
		Assert.AreEqual(0, userGroup.c?.Count ?? 0, "shadow must be cascaded out when canonical anchor is deleted");
	}

	[TestMethod]
	public async Task User_Groups_And_Memberships_Survive_Force_Rescan()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var anchorId = await GetCanonicalAnchorIdByName(svc, "getUser");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Outlive" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });

		// Принудительный рескан: проходит и сшивка Id, и перенос UserGroups+Memberships.
		var listed = await ListAsync(svc, forceRescan: true);
		var userGroups = RootGroupsByKind(listed, "user");
		Assert.AreEqual(1, userGroups.Count);
		Assert.AreEqual(grp.id, userGroups[0].id);
		Assert.AreEqual(1, userGroups[0].c?.Count ?? 0);
		Assert.AreEqual(anchorId, userGroups[0].c[0].aid);
	}

	[TestMethod]
	public async Task Lost_Anchor_On_Rescan_Goes_To_Lost_Bucket_And_Marks_Shadow()
	{
		// Документирующий тест нового поведения (этап 2): membership на якорь,
		// который при сшивке Id потерялся, НЕ выкидывается молча — якорь уезжает
		// в системный _Lost-bucket, а в user-группе остаётся теневой узел
		// с маркером lostAnchor. Это даёт пользователю видимость потери прямо
		// в его рабочих папках, и возможность recover-ить или discard-нуть.
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		await ListAsync(svc);

		var listUsersId = await GetCanonicalAnchorIdByName(svc, "listUsers");
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "WillLoseMember" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = listUsersId, groupId = grp.id });

		// listUsers исчезает в фикстуре removed (только в schema.graphql).
		CopyFixtureInto("removed");
		var listed = await ListAsync(svc, forceRescan: true);

		// User-группа сама уцелела — и НЕ считается среди auto-групп.
		var userGroups = RootGroupsByKind(listed, "user");
		Assert.AreEqual(1, userGroups.Count, "the user group itself must survive");
		Assert.AreEqual(grp.id, userGroups[0].id);

		// Внутри неё shadow-узел с маркером lostAnchor (а не отсутствие — membership перенесён).
		Assert.AreEqual(1, userGroups[0].c?.Count ?? 0, "shadow with lost marker must remain in the user group");
		var shadow = userGroups[0].c[0];
		Assert.AreEqual("lostAnchor", shadow.sys, "shadow must carry the lostAnchor marker");
		Assert.AreEqual(listUsersId, shadow.aid, "shadow must point at the original (now-lost) anchor id");

		// Появилась системная группа _Lost.
		var lostBucket = listed.r.FirstOrDefault(n => string.Equals(n.sys, "lost", StringComparison.OrdinalIgnoreCase));
		Assert.IsNotNull(lostBucket, "_Lost bucket must appear");
		Assert.IsTrue(lostBucket.c.Any(x => x.id == listUsersId), "lost anchor must be inside _Lost");
	}
}
