using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты multi-membership с дополнением AUTO-групп пользовательскими ссылками,
/// и взаимодействия rebind с shadow-узлами.
///
/// Покрывает интерпретацию C: один и тот же якорь может одновременно лежать
/// в auto-группе gqlField (где уже есть gql+ts pair) и в пользовательской папке;
/// при rebind canonical-якоря все его shadow-копии в дереве остаются валидными;
/// при исчезновении auto-группы (например, после переименования поля) её
/// membership-ы скрываются молча и автоматически "оживают" при возврате имени.
/// </summary>
[TestClass]
public class AutoGroupMembershipTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");
	private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "fixtures");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-agm-" + Guid.NewGuid().ToString("N"));
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
		catch { }
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

	private static TreeNodeClientV2 FindGroupByName(ListTreeResultV2 res, string name, string gkOrNull = null)
	{
		TreeNodeClientV2 found = null;
		Walk(res?.r, n =>
		{
			if (found != null) return;
			if (!string.Equals(n.t, "group", StringComparison.OrdinalIgnoreCase)) return;
			if (n.n != name) return;
			if (gkOrNull == null && string.Equals(n.gk, "user", StringComparison.OrdinalIgnoreCase)) return;
			if (gkOrNull != null && !string.Equals(n.gk, gkOrNull, StringComparison.OrdinalIgnoreCase)) return;
			found = n;
		});
		return found;
	}

	private static TreeNodeClientV2 FindCanonicalAnchorByName(ListTreeResultV2 res, string name, string kind = "gqlField")
	{
		TreeNodeClientV2 found = null;
		Walk(res?.r, n =>
		{
			if (found != null) return;
			if (!string.Equals(n.t, "anchor", StringComparison.OrdinalIgnoreCase)) return;
			if (n.n != name || n.k != kind) return;
			if (!string.IsNullOrEmpty(n.aid)) return; // shadow — пропускаем
			found = n;
		});
		return found;
	}

	// =========================================================================

	[TestMethod]
	public async Task Add_Anchor_To_GqlField_Auto_Group_Renders_Shadow_There()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var firstList = await ListAsync(svc);

		// Ищем auto-группу gqlField:getUser в дереве (вложена в gqlType:Query).
		// Берём её id напрямую — это auto-group id вида "gqlField:<hash>".
		TreeNodeClientV2 gqlFieldGroup = null;
		Walk(firstList.r, n =>
		{
			if (gqlFieldGroup != null) return;
			if (!string.Equals(n.t, "group", StringComparison.OrdinalIgnoreCase)) return;
			if (n.n != "getUser") return;
			if (n.id == null || !n.id.StartsWith("gqlField:", StringComparison.Ordinal)) return;
			gqlFieldGroup = n;
		});
		Assert.IsNotNull(gqlFieldGroup, "auto-group gqlField:getUser must exist in tree");

		// Возьмём какой-то посторонний якорь — например, listUsers gqlField — и
		// положим его в группу getUser, чтобы он показывался "связанным".
		var listUsersAnchor = FindCanonicalAnchorByName(firstList, "listUsers");
		Assert.IsNotNull(listUsersAnchor, "listUsers canonical anchor must exist");

		var add = await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams
		{
			anchorId = listUsersAnchor.id,
			groupId = gqlFieldGroup.id,
		});
		Assert.AreEqual(true, add.ok, add.m);

		var listed2 = await ListAsync(svc, forceRescan: false);
		TreeNodeClientV2 fieldGroupAfter = null;
		Walk(listed2.r, n =>
		{
			if (fieldGroupAfter != null) return;
			if (n.id == gqlFieldGroup.id) fieldGroupAfter = n;
		});
		Assert.IsNotNull(fieldGroupAfter, "auto-group must still exist after addToGroup");

		var shadow = fieldGroupAfter.c?.FirstOrDefault(c => string.Equals(c.aid, listUsersAnchor.id, StringComparison.Ordinal));
		Assert.IsNotNull(shadow, "shadow for listUsers must appear inside gqlField:getUser group");
		Assert.AreNotEqual(listUsersAnchor.id, shadow.id, "shadow.id must be a synthetic memberOf-id");
		StringAssert.StartsWith(shadow.id, "memberOf:");
	}

	[TestMethod]
	public async Task Same_Anchor_In_User_Group_And_Auto_Group_Simultaneously()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var firstList = await ListAsync(svc);

		var listUsersAnchor = FindCanonicalAnchorByName(firstList, "listUsers");
		Assert.IsNotNull(listUsersAnchor);

		TreeNodeClientV2 gqlFieldGetUser = null;
		Walk(firstList.r, n =>
		{
			if (gqlFieldGetUser != null) return;
			if (string.Equals(n.t, "group", StringComparison.OrdinalIgnoreCase)
				&& n.n == "getUser"
				&& n.id != null && n.id.StartsWith("gqlField:", StringComparison.Ordinal))
				gqlFieldGetUser = n;
		});
		Assert.IsNotNull(gqlFieldGetUser);

		var userGroup = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Релевантные методы" })).g;

		// Один и тот же якорь — в auto-группу и в user-группу.
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = listUsersAnchor.id, groupId = gqlFieldGetUser.id });
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = listUsersAnchor.id, groupId = userGroup.id });

		var listed = await ListAsync(svc, forceRescan: false);

		var totalShadows = 0;
		Walk(listed.r, n =>
		{
			if (string.Equals(n.t, "anchor", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(n.aid, listUsersAnchor.id, StringComparison.Ordinal))
				totalShadows++;
		});
		Assert.AreEqual(2, totalShadows, "the same anchor must produce two shadows: one in user-group, one in auto-group");

		// Канонический по-прежнему один.
		var canonical = 0;
		Walk(listed.r, n =>
		{
			if (string.Equals(n.t, "anchor", StringComparison.OrdinalIgnoreCase)
				&& n.id == listUsersAnchor.id
				&& string.IsNullOrEmpty(n.aid))
				canonical++;
		});
		Assert.AreEqual(1, canonical);
	}

	[TestMethod]
	public async Task UpdateAnchor_Refreshes_All_Shadows_With_New_Offsets()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var firstList = await ListAsync(svc);

		var anchor = FindCanonicalAnchorByName(firstList, "getUser");
		Assert.IsNotNull(anchor);
		var oldStart = anchor.s;

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Папка" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchor.id, groupId = grp.id });

		// Меняем файлы на shifted (offsets сдвигаются), но не делаем рескан —
		// updateAnchor сам пересоберёт forests.
		CopyFixtureInto("shifted");
		var upd = await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = anchor.id });
		Assert.IsNotNull(upd?.u, "updateAnchor must return updated node");
		Assert.AreEqual(anchor.id, upd.u.id, "Id must be preserved through rebind");
		Assert.AreNotEqual(oldStart, upd.u.s, "offset must change after rebind onto shifted source");

		var listed = await ListAsync(svc, forceRescan: false);

		// canonical обновился
		var canonical = FindCanonicalAnchorByName(listed, "getUser");
		Assert.AreEqual(upd.u.s, canonical.s);
		// shadow тоже отражает новые offsets — он клонируется из canonical при rebuild
		var userGroupNode = FindGroupByName(listed, "Папка", gkOrNull: "user");
		Assert.IsNotNull(userGroupNode);
		var shadow = userGroupNode.c.Single();
		Assert.AreEqual(anchor.id, shadow.aid);
		Assert.AreEqual(canonical.s, shadow.s, "shadow.s must follow canonical.s after rebind");
		Assert.AreEqual(canonical.f, shadow.f);
	}

	[TestMethod]
	public async Task Auto_Group_Disappear_Hides_Membership_And_Reappear_Restores_It()
	{
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var firstList = await ListAsync(svc);

		// Кидаем listUsers в auto-группу gqlField:getUser.
		var listUsers = FindCanonicalAnchorByName(firstList, "listUsers");
		TreeNodeClientV2 gqlFieldGetUser = null;
		Walk(firstList.r, n =>
		{
			if (gqlFieldGetUser != null) return;
			if (string.Equals(n.t, "group", StringComparison.OrdinalIgnoreCase)
				&& n.n == "getUser"
				&& n.id != null && n.id.StartsWith("gqlField:", StringComparison.Ordinal))
				gqlFieldGetUser = n;
		});
		Assert.IsNotNull(gqlFieldGetUser);
		var savedGroupId = gqlFieldGetUser.id;

		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = listUsers.id, groupId = savedGroupId });

		// Переименовываем getUser → findUserById. Auto-группа gqlField:getUser исчезает,
		// но membership с её Id остаётся в snapshot.
		CopyFixtureInto("rename");
		var afterRename = await ListAsync(svc, forceRescan: true);

		var groupAfterRename = (TreeNodeClientV2)null;
		Walk(afterRename.r, n =>
		{
			if (groupAfterRename != null) return;
			if (n.id == savedGroupId) groupAfterRename = n;
		});
		Assert.IsNull(groupAfterRename, "old auto-group with that id must NOT be in tree after rename");

		// Ни один shadow listUsers внутри (несуществующей) старой группы тоже не должен показываться.
		var shadowsForListUsersInOldGroup = 0;
		Walk(afterRename.r, n =>
		{
			if (string.Equals(n.t, "anchor", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(n.aid, listUsers.id, StringComparison.Ordinal)
				&& n.id != null && n.id.StartsWith($"memberOf:{savedGroupId}::", StringComparison.Ordinal))
				shadowsForListUsersInOldGroup++;
		});
		Assert.AreEqual(0, shadowsForListUsersInOldGroup, "shadow tied to disappeared auto-group must hide");

		// Возвращаем фикстуру обратно — auto-группа возвращается с тем же hash-Id.
		CopyFixtureInto("basic");
		var afterRevert = await ListAsync(svc, forceRescan: true);

		TreeNodeClientV2 restoredGroup = null;
		Walk(afterRevert.r, n =>
		{
			if (restoredGroup != null) return;
			if (n.id == savedGroupId) restoredGroup = n;
		});
		Assert.IsNotNull(restoredGroup, "auto-group with the same id must reappear after the rename was reverted");

		var revivedShadow = restoredGroup.c?.FirstOrDefault(c => string.Equals(c.aid, listUsers.id, StringComparison.Ordinal));
		Assert.IsNotNull(revivedShadow, "membership must come back to life when its auto-group reappears");
	}

	[TestMethod]
	public async Task Add_New_Manual_Anchor_Then_To_Group_Shows_It_In_Both_Places()
	{
		// Сценарий "реальная точка в группу": клиент сначала addAnchorAt (создаёт manual,
		// дублирующий существующий auto или нет — зависит от позиции), потом addAnchorToGroup.
		// В наших фикстурах позиции func_line всегда покрыты auto, поэтому addAnchorAt вернёт
		// alreadyExists=true и existing-Id. Это всё равно покрывает workflow: "указали точку
		// в коде → положили в группу".
		CopyFixtureInto("basic");
		var svc = await CreateInitializedService();
		var firstList = await ListAsync(svc);

		var schemaPath = Path.Combine(_tempFolder, "schema.graphql");
		var text = await File.ReadAllTextAsync(schemaPath);
		var idx = text.IndexOf("getUser", StringComparison.Ordinal);
		Assert.IsTrue(idx >= 0);
		var insideOffset = idx + 3;

		var addAnchor = await svc.AddAnchorAtAsync(new AddAnchorParams
		{
			filePath = schemaPath,
			offset = insideOffset,
		});
		Assert.IsNotNull(addAnchor?.a);
		var anchorId = addAnchor.a.id;

		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Тут" })).g;
		var addToGroup = await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = anchorId, groupId = grp.id });
		Assert.AreEqual(true, addToGroup.ok, addToGroup.m);

		var listed = await ListAsync(svc, forceRescan: false);

		// Якорь виден в auto-структуре (canonical)
		var canonical = FindCanonicalAnchorByName(listed, "getUser");
		Assert.IsNotNull(canonical);
		Assert.AreEqual(anchorId, canonical.id);

		// И в группе как shadow
		var userGroupNode = FindGroupByName(listed, "Тут", gkOrNull: "user");
		Assert.IsNotNull(userGroupNode);
		var shadow = userGroupNode.c.Single(c => c.aid == anchorId);
		Assert.IsNotNull(shadow);
		StringAssert.StartsWith(shadow.id, "memberOf:");
	}
}
