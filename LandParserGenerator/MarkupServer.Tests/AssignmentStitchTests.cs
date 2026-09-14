using System.Collections.Generic;
using System.Linq;
using MarkupServer;
using VPTree;

namespace MarkupServer.Tests;

/// <summary>
/// Слой 2 сшивки Id auto-якорей: назначение один-к-одному с отказом (VPTreeLib.AssignmentRebind) вместо жадного
/// выбора по возрастанию d1. Синтетические якоря без файлов и парсеров: сшивка — функция от двух списков TreeNode.
/// Предусловия сценариев проверяются через ту же метрику (Dist.AnchorDistance), чтобы тест объяснял себя при её смене.
/// </summary>
[TestClass]
public class AssignmentStitchTests
{
	private static TreeNode Gql(string name, string parent = "Query", string returns = "User", List<Arg> args = null, int offset = 0)
		=> new()
		{
			Id = $"gql:{parent}.{name}",
			Language = "gql",
			NodeType = "anchor",
			AnchorKind = "gqlField",
			Name = name,
			MethodNameNorm = name,
			ParentNameRaw = parent,
			ParentNameNorm = parent,
			ReturnTypeNorm = returns,
			Args = args ?? new List<Arg> { new() { NameNorm = "id", TypeNorm = "ID!" } },
			Filepath = "schema/users.graphql",
			StartOffset = offset,
			EndOffset = offset + 10,
		};

	private static MethodAnchor Legacy(TreeNode n) => new()
	{
		Id = n.Id,
		MethodNameNorm = n.MethodNameNorm,
		ParentNameNorm = n.ParentNameNorm,
		ReturnTypeNorm = n.ReturnTypeNorm,
		Args = n.Args.Select(a => new MethodAnchor.Arg { TypeNorm = a.TypeNorm, NameNorm = a.NameNorm }).ToList(),
	};

	private static double D(TreeNode a, TreeNode b) => Dist.AnchorDistance(Legacy(a), Legacy(b), new Dist.Weights());

	[TestMethod]
	public void Competing_Anchors_Are_Assigned_Jointly_Not_Greedily()
	{
		// Старые: A = getUserAAX, B = getUserXY. Новые: K = getUserX (ближе к B, но и к A близко), C = getUserAAAAAX (второй
		// кандидат A, далёкий от B), D — далеко от всех. Поточечно A неоднозначна (K и C почти равны), B → K;
		// назначением: A → C, B → K — обе точки привязаны, ничего не украдено.
		var prevA = Gql("getUserAAX", offset: 0);
		var prevB = Gql("getUserXY", offset: 100);
		var freshK = Gql("getUserX", offset: 0);
		var freshC = Gql("getUserAAAAAX", offset: 100);
		var freshD = Gql("listOrdersByCustomer", returns: "[Order!]!", offset: 200);

		double aK = D(prevA, freshK), aC = D(prevA, freshC), bK = D(prevB, freshK), bC = D(prevB, freshC);
		string dists = $"d(A,K)={aK:F4} d(A,C)={aC:F4} d(B,K)={bK:F4} d(B,C)={bC:F4}";
		Assert.IsTrue(bK < aK && aK < aC && aC <= 0.30 && bC > aC + aK, "precondition: " + dists);

		var svc = new LandService();
		var res = svc.StitchAutoAnchorIdsFromPrevious(new[] { prevA, prevB }, new[] { freshK, freshC, freshD });
		Assert.AreEqual(0, res.MatchedExact.Count);
		var fuzzy = res.MatchedFuzzy.ToDictionary(x => x.PrevId, x => x.FreshIdReplaced);
		Assert.IsTrue(fuzzy.ContainsKey("gql:Query.getUserAAX"), "A must be matched (lost: " + string.Join(",", res.Lost) + "); " + dists);
		Assert.IsTrue(fuzzy.ContainsKey("gql:Query.getUserXY"), "B must be matched (lost: " + string.Join(",", res.Lost) + "); " + dists);
		Assert.AreEqual("gql:Query.getUserAAAAAX", fuzzy["gql:Query.getUserAAX"], dists);
		Assert.AreEqual("gql:Query.getUserX", fuzzy["gql:Query.getUserXY"], dists);
		Assert.AreEqual(0, res.Lost.Count);
		CollectionAssert.AreEqual(new[] { "gql:Query.listOrdersByCustomer" }, res.Fresh.ToList());
		Assert.AreEqual("gql:Query.getUserAAX", freshC.Id);
		Assert.AreEqual("gql:Query.getUserXY", freshK.Id);
	}

	[TestMethod]
	public void Exact_Tie_On_Single_Candidate_Leaves_Both_Lost()
	{
		// Две старые точки на одном расстоянии от единственного кандидата: ничья → ни одна не крадёт, обе Lost.
		var prevA = Gql("getUserA", offset: 0);
		var prevB = Gql("getUserB", offset: 100);
		var freshK = Gql("getUserX", offset: 0);
		Assert.AreEqual(D(prevA, freshK), D(prevB, freshK), 1e-12, "precondition: symmetric edits");
		Assert.IsTrue(D(prevA, freshK) <= 0.30, "precondition: within tau");

		var svc = new LandService();
		var res = svc.StitchAutoAnchorIdsFromPrevious(new[] { prevA, prevB }, new[] { freshK });
		Assert.AreEqual(0, res.MatchedFuzzy.Count, string.Join(",", res.MatchedFuzzy.Select(x => x.PrevId)));
		CollectionAssert.AreEquivalent(new[] { "gql:Query.getUserA", "gql:Query.getUserB" }, res.Lost.ToList());
		CollectionAssert.AreEqual(new[] { "gql:Query.getUserX" }, res.Fresh.ToList());
	}

	[TestMethod]
	public void Single_Anchor_Follows_Pointwise_Rule()
	{
		var prev = Gql("getUser");
		var near = Gql("getUserById");
		var far = Gql("listOrdersByCustomer", returns: "[Order!]!", offset: 200);
		double d1 = D(prev, near), d2 = D(prev, far);
		Assert.IsTrue(d1 <= 0.30 && (d2 - d1) / d2 >= 0.30, $"precondition: d1={d1:F4} d2={d2:F4}");

		var svc = new LandService();
		var res = svc.StitchAutoAnchorIdsFromPrevious(new[] { prev }, new[] { near, far });
		Assert.AreEqual(1, res.MatchedFuzzy.Count);
		Assert.AreEqual("gql:Query.getUserById", res.MatchedFuzzy[0].FreshIdReplaced);
		Assert.AreEqual("gql:Query.getUser", near.Id);
		Assert.AreEqual(0, res.Lost.Count);
	}

	[TestMethod]
	public void Exact_Key_Layer_Still_Runs_First()
	{
		var prev = Gql("getUser", offset: 0);
		var same = Gql("getUser", offset: 500);
		var svc = new LandService();
		var res = svc.StitchAutoAnchorIdsFromPrevious(new[] { prev }, new[] { same });
		Assert.AreEqual(1, res.MatchedExact.Count);
		Assert.AreEqual(0, res.MatchedFuzzy.Count);
		Assert.AreEqual("gql:Query.getUser", same.Id);
	}
}
