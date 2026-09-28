using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkupServer;
using VPTree;

namespace MarkupServer.Tests;

/// <summary>
/// Спорные компоненты перепривязки: сохранение при рескане, проценты с учётом выбора пользователя,
/// применение решения (передача Id как при сшивке), кандидаты с процентами для одиночной точки.
/// </summary>
[TestClass]
public class DisputeTests
{
	private static string GrammarsFolder => Path.Combine(AppContext.BaseDirectory, "grammars");

	private string _tempFolder;

	[TestInitialize]
	public void Setup()
	{
		_tempFolder = Path.Combine(Path.GetTempPath(), "markup-tests-dispute-" + Guid.NewGuid().ToString("N"));
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

	// ---- схемы: две точки на одного кандидата, кандидаты для одиночной точки. Правки похожи, но расстояния
	//      не равны: метрика учитывает соседей по типу, поэтому тесты сверяют проценты с расстояниями, а не ничью.

	private const string Types = "type User {\n  id: ID!\n  name: String!\n}\n\n";
	private const string SchemaAB = Types + "type Query {\n  getUserA(id: ID!): User\n  getUserB(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";
	private const string SchemaX = Types + "type Query {\n  getUserX(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";
	private const string SchemaA = Types + "type Query {\n  getUserA(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";
	private const string SchemaXY = Types + "type Query {\n  getUserX(id: ID!): User\n  getUserY(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";

	private void WriteSchema(string text) => File.WriteAllText(Path.Combine(_tempFolder, "schema.graphql"), text);

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

	private Task<ListTreeResultV2> ListAsync(LandService svc, bool forceRescan = true)
		=> svc.ListAnchorsAsync(new ListTreeParams { folderPath = _tempFolder, forceRescan = forceRescan, preferCache = !forceRescan });

	private static List<TreeNodeClientV2> All(ListTreeResultV2 res)
	{
		var sink = new List<TreeNodeClientV2>();
		void Walk(List<TreeNodeClientV2> nodes)
		{
			foreach (var n in nodes ?? new List<TreeNodeClientV2>())
			{
				if (n == null) continue;
				sink.Add(n);
				Walk(n.c);
			}
		}
		Walk(res?.r);
		return sink;
	}

	private static TreeNodeClientV2 Canonical(ListTreeResultV2 res, string name)
		=> All(res).FirstOrDefault(x => x.t == "anchor" && x.n == name && string.IsNullOrEmpty(x.aid) && x.sys == null);

	private static TreeNodeClientV2 Root(ListTreeResultV2 res, string sys)
		=> (res?.r ?? new List<TreeNodeClientV2>()).FirstOrDefault(n => n.sys == sys);

	private async Task<(LandService Svc, string A, string B, UserGroupResultV2 Group)> TieScenario(bool withUserData)
	{
		WriteSchema(SchemaAB);
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);
		var a = Canonical(listed, "getUserA").id;
		var b = Canonical(listed, "getUserB").id;
		UserGroupResultV2 grp = null;
		if (withUserData)
		{
			grp = await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Пользователи" });
			await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = a, groupId = grp.g.id });
			await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = b, groupId = grp.g.id });
		}
		WriteSchema(SchemaX);
		return (svc, a, b, grp);
	}

	// =====================================================================================

	[TestMethod]
	public void Stitch_Records_Disputed_Component_On_Tie()
	{
		TreeNode Gql(string name, int offset) => new()
		{
			Id = $"gql:Query.{name}", Language = "gql", NodeType = "anchor", AnchorKind = "gqlField", Name = name,
			MethodNameNorm = name, ParentNameRaw = "Query", ParentNameNorm = "Query", ReturnTypeNorm = "User",
			Args = new List<Arg> { new() { NameNorm = "id", TypeNorm = "ID!" } }, Filepath = "schema/users.graphql",
			StartOffset = offset, EndOffset = offset + 10,
		};
		var svc = new LandService();
		var res = svc.StitchAutoAnchorIdsFromPrevious(new[] { Gql("getUserA", 0), Gql("getUserB", 100) }, new[] { Gql("getUserX", 0) });
		Assert.AreEqual(1, res.Disputes.Count, "tie of two points on one candidate is a disputed component");
		var d = res.Disputes[0];
		Assert.AreEqual(2, d.Points.Count);
		Assert.IsTrue(d.Points.All(p => p.Status == AssignmentRebind.Status.Ambiguous));
		Assert.IsTrue(d.Points.All(p => p.Candidates.Count == 1 && p.Candidates[0].Fresh.Name == "getUserX"));
		Assert.AreEqual(0.30 / 0.70, d.RejectCost, 1e-12);
		CollectionAssert.AreEquivalent(new[] { "gql:Query.getUserA", "gql:Query.getUserB" }, res.LostCandidates.Keys.ToList());
	}

	[TestMethod]
	public async Task No_Dispute_Without_User_Data()
	{
		var (svc, _, _, _) = await TieScenario(withUserData: false);
		var after = await ListAsync(svc);
		Assert.IsNull(after.dq, "nothing of the user's is at stake");
		Assert.IsNull(Root(after, "disputes"));
	}

	[TestMethod]
	public async Task Dispute_Appears_With_User_Data_And_Is_Persisted()
	{
		var (svc, a, b, _) = await TieScenario(withUserData: true);
		var after = await ListAsync(svc);
		Assert.AreEqual(1, after.dq);
		var root = Root(after, "disputes");
		Assert.IsNotNull(root, "_Спорные root must appear");
		StringAssert.StartsWith(root.n, "_Спорные (1)");
		Assert.AreEqual(1, root.c.Count);
		Assert.AreEqual("dispute", root.c[0].sys);
		StringAssert.Contains(root.c[0].n, "getUserA");

		var lost = Root(after, "lost");
		Assert.IsNotNull(lost);
		var lostA = lost.c.FirstOrDefault(x => x.id == a);
		Assert.IsNotNull(lostA, "point with user data waits in _Lost");
		StringAssert.Contains(lostA.n, "спорно");

		// снимок переживает перезапуск: новый сервис читает кэш
		var svc2 = await CreateInitializedService();
		var cached = await ListAsync(svc2, forceRescan: false);
		Assert.AreEqual(1, cached.dq);
		Assert.IsNotNull(Root(cached, "disputes"));
	}

	[TestMethod]
	public async Task GetDispute_Gives_Percentages_And_Respects_User_Choice()
	{
		var (svc, a, b, _) = await TieScenario(withUserData: true);
		await ListAsync(svc);
		var view = await svc.GetDisputeAsync(new GetDisputeParams());
		Assert.IsNull(view.m, view.m);
		Assert.AreEqual(2, view.points.Count);
		Assert.IsTrue(view.exact);
		foreach (var pt in view.points)
		{
			Assert.AreEqual(2, pt.o.Count, "one candidate + «не переносить»");
			Assert.AreEqual(1.0, pt.o.Sum(o => o.p), 1e-9, "row percentages sum to 100 %");
			Assert.IsNull(pt.o[1].t);
		}
		var x = view.points[0].o[0].t;
		Assert.AreEqual(x, view.points[1].o[0].t, "both points compete for the same field");
		var closer = view.points.OrderBy(pt => pt.o[0].d).First();
		var farther = view.points.OrderBy(pt => pt.o[0].d).Last();
		Assert.IsTrue(closer.o[0].p > farther.o[0].p, "the closer point gets the larger share of the field");
		Assert.AreEqual(1.0, closer.o[0].p + farther.o[0].p, 0.02, "the field goes to one of them almost surely");
		Assert.IsTrue(view.top.Count >= 2);

		// выбор: A → X. У A проценты без её выбора (как до выбора), у B поле X занято.
		var pinned = await svc.GetDisputeAsync(new GetDisputeParams
		{
			disputeId = view.id,
			fixedChoices = new List<DisputeChoiceParam> { new() { point = a, target = x } },
		});
		var pa = pinned.points.Single(p => p.id == a);
		var pb = pinned.points.Single(p => p.id == b);
		Assert.AreEqual(x, pa.pin);
		Assert.AreEqual(view.points.Single(p => p.id == a).o[0].p, pa.o[0].p, 1e-9, "leave-one-out: the model's view of A");
		Assert.AreEqual(0.0, pb.o.Single(o => o.t == x).p, 1e-12, "X is taken by the user's choice");
		Assert.AreEqual(1.0, pb.o.Single(o => o.t == null).p, 1e-12);
		Assert.AreEqual(x, pinned.top[0].a[pinned.points.IndexOf(pa)]);

		// несовместимый выбор
		var conflict = await svc.GetDisputeAsync(new GetDisputeParams
		{
			disputeId = view.id,
			fixedChoices = new List<DisputeChoiceParam> { new() { point = a, target = x }, new() { point = b, target = x } },
		});
		Assert.IsNotNull(conflict.m);
		Assert.AreEqual(0, conflict.top.Count);
	}

	[TestMethod]
	public async Task Resolve_Transfers_Id_And_Keeps_The_Rest_In_Lost()
	{
		var (svc, a, b, grp) = await TieScenario(withUserData: true);
		await ListAsync(svc);
		var view = await svc.GetDisputeAsync(new GetDisputeParams());
		var x = view.points[0].o[0].t;

		var bad = await svc.ResolveDisputeAsync(new ResolveDisputeParams
		{
			disputeId = view.id,
			choices = new List<DisputeChoiceParam> { new() { point = a, target = x }, new() { point = b, target = x } },
		});
		Assert.AreEqual(false, bad.ok, "one field for two points is rejected");

		var res = await svc.ResolveDisputeAsync(new ResolveDisputeParams
		{
			disputeId = view.id,
			choices = new List<DisputeChoiceParam> { new() { point = a, target = x }, new() { point = b, target = null } },
		});
		Assert.AreEqual(true, res.ok, res.m);
		Assert.AreEqual(0, res.left);

		var after = await ListAsync(svc, forceRescan: false);
		Assert.IsNull(after.dq);
		Assert.IsNull(Root(after, "disputes"));
		var live = Canonical(after, "getUserX");
		Assert.AreEqual(a, live.id, "getUserX inherits A's Id, as the stitch would have done");
		var lost = Root(after, "lost");
		Assert.IsNotNull(lost);
		Assert.IsFalse(lost.c.Any(n => n.id == a), "A is no longer lost");
		Assert.IsTrue(lost.c.Any(n => n.id == b), "B (with membership) stays in _Lost");
		var groupNode = after.r.Single(n => n.id == grp.g.id);
		Assert.IsTrue(groupNode.c.Any(n => n.aid == a && n.sys == null), "membership of A now points to a live anchor");

		// решение сохраняется: следующий рескан не возвращает группу
		var rescan = await ListAsync(svc);
		Assert.IsNull(rescan.dq);
		Assert.AreEqual(a, Canonical(rescan, "getUserX").id);
	}

	[TestMethod]
	public async Task Recover_Outside_The_Window_Drops_The_Dispute()
	{
		var (svc, a, b, _) = await TieScenario(withUserData: true);
		var listed = await ListAsync(svc);
		var x = Canonical(listed, "getUserX").id;

		var cands = await svc.GetLostCandidatesAsync(new LostCandidatesParams { lostAnchorId = a });
		Assert.IsNull(cands.m, cands.m);
		Assert.AreEqual(1, cands.cs.Count);
		Assert.AreEqual(x, cands.cs[0].n.id);
		Assert.AreEqual(1.0, cands.cs.Sum(c => c.p) + cands.pz.Value, 1e-9);

		await svc.RecoverLostAnchorAsync(new RecoverLostAnchorParams { lostAnchorId = a, newAnchorId = x });
		var after = await ListAsync(svc, forceRescan: false);
		Assert.IsNull(after.dq, "the point was resolved elsewhere — the dispute is dropped");
	}

	// ---- Update Anchor: тот же конвейер, что полный рескан

	private const string SchemaGetUser = Types + "type Query {\n  getUser(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";
	private const string SchemaGetUserById = Types + "type Query {\n  getUserById(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";
	private const string SchemaUserUsers = Types + "type Query {\n  getUser(id: ID!): User\n  getUsers(limit: Int!): [User!]!\n}\n";
	private const string SchemaUserAllUsers = Types + "type Query {\n  getUser(id: ID!): User\n  getAllUsers(limit: Int!): [User!]!\n}\n";

	[TestMethod]
	public async Task UpdateAnchor_Confident_Point_Moves_Without_A_Window()
	{
		WriteSchema(SchemaGetUser);
		var svc = await CreateInitializedService();
		var a = Canonical(await ListAsync(svc), "getUser").id;

		WriteSchema(SchemaGetUserById);
		var res = await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = a });
		Assert.IsNotNull(res.u, res.rs);
		Assert.AreEqual(a, res.u.id, "the point keeps its Id");
		Assert.AreEqual("getUserById", res.u.n);
		Assert.IsNull(res.st);
	}

	[TestMethod]
	public async Task UpdateAnchor_Lone_Uncertain_Point_Opens_Its_Dispute()
	{
		WriteSchema(SchemaA);
		var svc = await CreateInitializedService();
		var a = Canonical(await ListAsync(svc), "getUserA").id;

		WriteSchema(SchemaXY);   // getUserX и getUserY почти одинаково похожи на getUserA
		var res = await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = a });
		Assert.IsNull(res.u);
		Assert.AreEqual("dispute", res.st, res.rs);
		Assert.IsNotNull(res.did);

		var view = await svc.GetDisputeAsync(new GetDisputeParams { disputeId = res.did });
		Assert.AreEqual(1, view.points.Count, "no rivals — a one-point component");
		Assert.AreEqual(a, view.points[0].id);
		var names = view.points[0].o.Where(o => o.t != null).Select(o => o.n).ToList();
		CollectionAssert.IsSubsetOf(new[] { "getUserX", "getUserY" }, names);
		Assert.AreEqual(1.0, view.points[0].o.Sum(o => o.p), 1e-9);
	}

	[TestMethod]
	public async Task UpdateAnchor_With_Rival_Opens_The_Whole_Component()
	{
		var (svc, a, b, _) = await TieScenario(withUserData: false);   // без данных пользователя: рескан бы не спросил
		var res = await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = a });
		Assert.AreEqual("dispute", res.st, res.rs);
		var view = await svc.GetDisputeAsync(new GetDisputeParams { disputeId = res.did });
		CollectionAssert.AreEquivalent(new[] { a, b }, view.points.Select(pt => pt.id).ToList(), "the rival is in the window too");
	}

	[TestMethod]
	public async Task UpdateAnchor_Does_Not_Offer_A_Field_Held_By_Another_Point()
	{
		// getUsers переименован в getAllUsers; getUser не менялся. Поточечно ближайший к getUsers — getUser (одна буква),
		// но это поле занято неизменённой точкой getUser: предпроход точного ключа забирает его, кандидатом оно не будет.
		WriteSchema(SchemaUserUsers);
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);
		var user = Canonical(listed, "getUser").id;
		var users = Canonical(listed, "getUsers").id;

		WriteSchema(SchemaUserAllUsers);
		var res = await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = users });
		if (res.u != null)
			Assert.AreNotEqual("getUser", res.u.n, "must not steal the field of another point");
		if (res.st == "dispute")
		{
			var view = await svc.GetDisputeAsync(new GetDisputeParams { disputeId = res.did });
			Assert.IsFalse(view.points.SelectMany(pt => pt.o).Any(o => o.n == "getUser"), "held field is not a candidate");
		}

		var after = await ListAsync(svc, forceRescan: false);
		Assert.AreEqual(user, Canonical(after, "getUser").id, "the unchanged point keeps its field");
	}

	[TestMethod]
	public async Task Manual_Point_Merging_Into_An_Anchor_Keeps_Its_Membership_And_Comment()
	{
		WriteSchema(SchemaGetUser);
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);
		var auto = Canonical(listed, "getUser");

		// Удалили авто-якорь и вернули точку вручную: ручная точка на том же месте, подавление снято.
		await svc.DeleteAnchorAsync(new DeleteAnchorParams { anchorId = auto.id });
		var added = await svc.AddAnchorAtAsync(new AddAnchorParams { filePath = auto.f, offset = auto.s.Value + 1 });
		Assert.IsNotNull(added.a, added.m);
		var manualId = added.a.id;
		var grp = (await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Ручные" })).g;
		await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = manualId, groupId = grp.id });
		await svc.SetCommentAsync(new SetCommentParams { targetId = manualId, comment = "моя точка" });

		// При рескане авто-якорь сущности появляется снова, ручная точка встаёт на его место и сливается с ним.
		var after = await ListAsync(svc);
		var live = Canonical(after, "getUser");
		Assert.IsNotNull(live);
		var folder = after.r.Single(n => n.id == grp.id);
		var shadow = folder.c?.SingleOrDefault();
		Assert.IsNotNull(shadow, "membership must survive the merge");
		Assert.AreEqual(live.id, shadow.aid, "membership now points to the anchor the manual point merged into");
		Assert.IsNull(shadow.sys);
		Assert.AreEqual("моя точка", live.cm, "comment moves with the point");
	}

	// ---- уверенная пара в спорной компоненте ждёт решения по компоненте целиком (решение пользователя 2026-09-28)
	//      findUser → findUsers — уверенная пара (одна буква); getUserA и getUserB делят поле getUserX (ничья),
	//      и findUser связан с getUserX в пределах цены отказа, поэтому все три точки — одна компонента.

	private const string SchemaABF = Types + "type Query {\n  getUserA(id: ID!): User\n  getUserB(id: ID!): User\n  findUser(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";
	private const string SchemaXF = Types + "type Query {\n  getUserX(id: ID!): User\n  findUsers(id: ID!): User\n  listOrders(limit: Int!): [User!]!\n}\n";

	private static TreeNode QueryField(string name, int offset) => new()
	{
		Id = $"gql:Query.{name}", Language = "gql", NodeType = "anchor", AnchorKind = "gqlField", Name = name,
		MethodNameNorm = name, ParentNameRaw = "Query", ParentNameNorm = "Query", ReturnTypeNorm = "User",
		Args = new List<Arg> { new() { NameNorm = "id", TypeNorm = "ID!" } }, Filepath = "schema/users.graphql",
		StartOffset = offset, EndOffset = offset + 10,
	};

	[TestMethod]
	public void Stitch_Holds_Confident_Pairs_Only_In_Components_The_User_Will_See()
	{
		TreeNode[] Prev() => new[] { QueryField("getUserA", 0), QueryField("getUserB", 100), QueryField("findUser", 200) };

		// компоненту увидят (данные пользователя у спорной getUserA): уверенная пара не применена
		var fresh = new[] { QueryField("getUserX", 0), QueryField("findUsers", 200) };
		var res = new LandService().StitchAutoAnchorIdsFromPrevious(Prev(), fresh, askUser: p => p.Name == "getUserA");
		CollectionAssert.AreEqual(new[] { "gql:Query.findUser" }, res.Held);
		Assert.AreEqual(0, res.MatchedFuzzy.Count);
		Assert.IsTrue(res.Lost.Contains("gql:Query.findUser"), "the held point waits among the lost");
		Assert.AreEqual("gql:Query.findUsers", fresh[1].Id, "the proposed place keeps its own Id");
		var pf = res.Disputes.Single().Points.Single(p => p.Prev.Name == "findUser");
		Assert.AreEqual(AssignmentRebind.Status.Accepted, pf.Status, "still a confident pair");
		Assert.AreSame(fresh[1], pf.ProposedFresh);
		Assert.IsNull(pf.AppliedFresh);

		// компоненту не увидят: уверенная пара применяется сразу, как раньше
		var fresh2 = new[] { QueryField("getUserX", 0), QueryField("findUsers", 200) };
		var res2 = new LandService().StitchAutoAnchorIdsFromPrevious(Prev(), fresh2, askUser: _ => false);
		Assert.AreEqual(0, res2.Held.Count);
		Assert.AreEqual("gql:Query.findUser", fresh2[1].Id, "the place took the point's Id");
		Assert.IsNotNull(res2.Disputes.Single().Points.Single(p => p.Prev.Name == "findUser").AppliedFresh);
	}

	private async Task<(LandService Svc, string A, string B, string F)> HeldScenario(bool membershipOnA)
	{
		WriteSchema(SchemaABF);
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc);
		var a = Canonical(listed, "getUserA").id;
		var b = Canonical(listed, "getUserB").id;
		var f = Canonical(listed, "findUser").id;
		if (membershipOnA)
		{
			var grp = await svc.CreateUserGroupAsync(new CreateUserGroupParams { name = "Пользователи" });
			await svc.AddAnchorToGroupAsync(new AddAnchorToGroupParams { anchorId = a, groupId = grp.g.id });
		}
		await svc.SetCommentAsync(new SetCommentParams { targetId = f, comment = "находит пользователя" });
		WriteSchema(SchemaXF);
		return (svc, a, b, f);
	}

	[TestMethod]
	public async Task Confident_Pair_Of_A_Shown_Dispute_Waits_And_Moves_On_Apply()
	{
		var (svc, a, b, f) = await HeldScenario(membershipOnA: true);
		var after = await ListAsync(svc);
		Assert.AreEqual(1, after.dq);
		var findUsers = Canonical(after, "findUsers");
		Assert.IsNotNull(findUsers);
		Assert.AreNotEqual(f, findUsers.id, "the confident pair is not applied until the whole component is confirmed");
		var lostF = Root(after, "lost")?.c.FirstOrDefault(x => x.id == f);
		Assert.IsNotNull(lostF, "the confident point with a comment waits in _Lost");
		StringAssert.Contains(lostF.n, "спорно");

		var view = await svc.GetDisputeAsync(new GetDisputeParams());
		Assert.AreEqual(3, view.points.Count);
		var pf = view.points.Single(p => p.id == f);
		Assert.AreEqual("accepted", pf.st);
		Assert.AreEqual(findUsers.id, pf.auto, "the window marks the confident pair with its proposed place");
		Assert.AreEqual(findUsers.id, view.top[0].a[view.points.IndexOf(pf)]);
		Assert.AreEqual("находит пользователя", pf.cm);

		// «Применить» лучший вариант: уверенная пара применяется вместе со всей компонентой
		var choices = view.points.Select((p, i) => new DisputeChoiceParam { point = p.id, target = view.top[0].a[i] }).ToList();
		var res = await svc.ResolveDisputeAsync(new ResolveDisputeParams { disputeId = view.id, choices = choices });
		Assert.AreEqual(true, res.ok, res.m);
		var applied = await ListAsync(svc, forceRescan: false);
		Assert.IsNull(applied.dq);
		Assert.AreEqual(f, Canonical(applied, "findUsers").id, "after confirmation the place carries the point's Id");
		Assert.AreEqual("находит пользователя", Canonical(applied, "findUsers").cm, "and its comment");
		Assert.IsFalse(Root(applied, "lost")?.c.Any(x => x.id == f) ?? false, "the point left _Lost");

		// решение сохраняется: следующий рескан не возвращает группу
		var rescan = await ListAsync(svc);
		Assert.IsNull(rescan.dq);
		Assert.AreEqual(f, Canonical(rescan, "findUsers").id);
	}

	[TestMethod]
	public async Task Confident_Pair_Is_Applied_At_Once_When_Nobody_Is_Asked()
	{
		// у спорных getUserA и getUserB нет данных пользователя — рескан ничего не спрашивает, уверенная пара применяется
		var (svc, _, _, f) = await HeldScenario(membershipOnA: false);
		var after = await ListAsync(svc);
		Assert.IsNull(after.dq);
		Assert.AreEqual(f, Canonical(after, "findUsers").id);
		Assert.AreEqual("находит пользователя", Canonical(after, "findUsers").cm);
	}

	[TestMethod]
	public async Task UpdateAnchor_On_A_Confident_Point_Of_A_Shown_Dispute_Opens_The_Window()
	{
		var (svc, _, _, f) = await HeldScenario(membershipOnA: true);
		var res = await svc.UpdateAnchorAsync(new UpdateAnchorParams { anchorId = f });
		Assert.AreEqual("dispute", res.st, res.rs);
		StringAssert.Contains(res.rs, "уверенно");
		var view = await svc.GetDisputeAsync(new GetDisputeParams { disputeId = res.did });
		Assert.AreEqual("accepted", view.points.Single(p => p.id == f).st);
	}

	// ---- решение для точек, которые сшивка уже применила: снимок собран вручную (снимки до 2026-09-28)

	private async Task<LandService> LoadCraftedSnapshot(PersistedMarkup markup)
	{
		Assert.IsTrue(AnchorStore.TrySave(_tempFolder, markup, out var err), err);
		var svc = await CreateInitializedService();
		var listed = await ListAsync(svc, forceRescan: false);
		Assert.AreEqual(true, listed.fc, "must load the crafted snapshot from cache");
		return svc;
	}

	private TreeNode Field(string id, string name, int offset, string comment = null) => new()
	{
		Id = id, Name = name, NodeType = "anchor", Language = "gql", AnchorKind = "gqlField", AnchorFamily = "callableMember",
		GqlTypeKind = "type", ParentNameRaw = "User", ParentNameNorm = "user", MethodNameNorm = name.ToLowerInvariant(),
		ReturnTypeNorm = "string", Args = new List<Arg>(), Filepath = Path.Combine(_tempFolder, "schema.graphql"),
		StartOffset = offset, EndOffset = offset + 10, Comment = comment,
	};

	private PersistedMarkup SwapMarkup()
	{
		// сшивка применила обе точки: name → поле fullName (узел N1), login → поле name (узел N2)
		var typeUser = new TreeNode
		{
			Id = "gql:type-user", Name = "type User", NodeType = "anchor", Language = "gql", AnchorKind = "gqlTypeDef",
			AnchorFamily = "recordLike", GqlTypeKind = "type", MethodNameNorm = "user", Filepath = Path.Combine(_tempFolder, "schema.graphql"),
			StartOffset = 0, EndOffset = 100,
		};
		var n1 = Field("gql:p-name", "fullName", 20, comment: "c-name");
		var n2 = Field("gql:p-login", "name", 40, comment: "c-login");
		return new PersistedMarkup
		{
			Anchors = new List<TreeNode> { typeUser, n1, n2 },
			UserGroups = new List<UserGroup> { new() { Id = "userGroup:g", Name = "Папка" } },
			Memberships = new List<UserGroupMembership> { new() { GroupId = "userGroup:g", AnchorId = "gql:p-name" } },
			Disputes = new List<DisputedComponent>
			{
				new()
				{
					Id = "dispute:swap",
					RejectCost = 0.30 / 0.70,
					Points = new List<DisputedPoint>
					{
						new()
						{
							AnchorId = "gql:p-name", Name = "name", Language = "gql", AnchorKind = "gqlField", ParentNameRaw = "User",
							Status = "accepted", AppliedFreshId = "gql:fresh-1", HasUserData = true,
							Candidates = new() { new() { AnchorId = "gql:p-login", Dist = 0.10 }, new() { AnchorId = "gql:p-name", Dist = 0.14 } },
						},
						new()
						{
							AnchorId = "gql:p-login", Name = "login", Language = "gql", AnchorKind = "gqlField", ParentNameRaw = "User",
							Status = "accepted", AppliedFreshId = "gql:fresh-2", HasUserData = false,
							Candidates = new() { new() { AnchorId = "gql:p-login", Dist = 0.11 }, new() { AnchorId = "gql:p-name", Dist = 0.18 } },
						},
					},
				},
			},
		};
	}

	[TestMethod]
	public async Task Crafted_Dispute_Percentages_Follow_The_Model()
	{
		var svc = await LoadCraftedSnapshot(SwapMarkup());
		var view = await svc.GetDisputeAsync(new GetDisputeParams { disputeId = "dispute:swap" });
		Assert.IsNull(view.m, view.m);
		var name = view.points[0];
		Assert.AreEqual("gql:p-name", name.auto, "applied automatically: the node now carrying the point's Id");
		Assert.AreEqual("gql:p-name", view.top[0].a[0]);
		Assert.AreEqual("gql:p-login", view.top[0].a[1]);
		Assert.AreEqual("c-name", name.cm);

		// те же числа из VPTreeLib напрямую
		var edges = new List<IReadOnlyList<(int, double)>>
		{
			new List<(int, double)> { (1, 0.10), (0, 0.14) },
			new List<(int, double)> { (1, 0.11), (0, 0.18) },
		};
		var dist = AssignmentProbabilities.Component(edges, AssignmentProbabilities.DefaultTemperature, 0.30 / 0.70);
		Assert.AreEqual(dist.Top[0].P, view.top[0].p, 1e-12);
		Assert.AreEqual(dist.RowP(0, 0), name.o.Single(o => o.t == "gql:p-name").p, 1e-12);
	}

	[TestMethod]
	public async Task Resolve_Swap_Of_Applied_Points_Moves_Ids_And_Comments()
	{
		var svc = await LoadCraftedSnapshot(SwapMarkup());
		var res = await svc.ResolveDisputeAsync(new ResolveDisputeParams
		{
			disputeId = "dispute:swap",
			choices = new List<DisputeChoiceParam>
			{
				new() { point = "gql:p-name", target = "gql:p-login" },   // точка name → узел N2 (поле name)
				new() { point = "gql:p-login", target = "gql:p-name" },   // точка login → узел N1 (поле fullName)
			},
		});
		Assert.AreEqual(true, res.ok, res.m);
		Assert.AreEqual(2, res.moved);

		var after = await ListAsync(svc, forceRescan: false);
		var fieldName = Canonical(after, "name");
		var fieldFullName = Canonical(after, "fullName");
		Assert.AreEqual("gql:p-name", fieldName.id, "point name now lives on the field 'name'");
		Assert.AreEqual("gql:p-login", fieldFullName.id);
		Assert.AreEqual("c-name", fieldName.cm, "comments follow the point, not the node");
		Assert.AreEqual("c-login", fieldFullName.cm);
		var folder = after.r.Single(n => n.id == "userGroup:g");
		Assert.AreEqual("name", folder.c.Single(n => n.aid == "gql:p-name").n, "membership follows the point");
		Assert.IsNull(after.dq);
	}

	[TestMethod]
	public async Task Resolve_Applied_Point_To_None_Restores_Fresh_Id_And_Moves_Point_To_Lost()
	{
		var svc = await LoadCraftedSnapshot(SwapMarkup());
		var res = await svc.ResolveDisputeAsync(new ResolveDisputeParams
		{
			disputeId = "dispute:swap",
			choices = new List<DisputeChoiceParam>
			{
				new() { point = "gql:p-name", target = null },
				new() { point = "gql:p-login", target = "gql:p-login" },
			},
		});
		Assert.AreEqual(true, res.ok, res.m);
		Assert.AreEqual(1, res.lost);

		var after = await ListAsync(svc, forceRescan: false);
		var fieldFullName = Canonical(after, "fullName");
		Assert.AreEqual("gql:fresh-1", fieldFullName.id, "the node gets back the Id it had before the stitch");
		Assert.IsNull(fieldFullName.cm);
		Assert.AreEqual("gql:p-login", Canonical(after, "name").id, "unchanged choice is a no-op");
		var lost = Root(after, "lost");
		Assert.IsNotNull(lost);
		var lostName = lost.c.Single(n => n.id == "gql:p-name");
		Assert.AreEqual("c-name", lostName.cm);
		var folder = after.r.Single(n => n.id == "userGroup:g");
		Assert.AreEqual("lostAnchor", folder.c.Single(n => n.aid == "gql:p-name").sys);
	}
}
