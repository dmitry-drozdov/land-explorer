using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;
using static VPTree.AssignmentRebind;

namespace Tests
{
	/// <summary>
	/// Назначение один-к-одному с отказом (VPTreeLib.AssignmentRebind): постановка по компонентам, приём по Δ,
	/// совпадение с поточечным правилом на точках без конкурентов, эквивалентность перебора и венгерского метода,
	/// k лучших назначений, детерминизм.
	/// </summary>
	[TestClass]
	public class AssignmentTests
	{
		private const double Tau = 0.30, Margin = 0.30;
		private static Options Opt(double reject = double.NaN, int enumLimit = 10, int kBest = 3)
			=> new Options { Tau = Tau, Margin = Margin, RejectCost = reject, EnumerateLimit = enumLimit, KBest = kBest };

		private static Decision Dec(Result r, int point) => r.Decisions.Single(d => d.Point == point);

		/// <summary>Поточечное правило сервера (LandService.DecideCore): d1 ≤ τ; при наличии d2 — (d2 − d1)/d2 ≥ m (d2 ≤ 0 → неоднозначно).</summary>
		private static Status Pointwise(List<Candidate> cands)
		{
			if (cands.Count == 0) return Status.Lost;
			var s = cands.OrderBy(c => c.Dist).ThenBy(c => c.Index).ToList();
			double d1 = s[0].Dist;
			if (d1 > Tau) return Status.Lost;
			if (s.Count > 1)
			{
				double d2 = s[1].Dist;
				double rel = d2 <= 0 ? 0.0 : (d2 - d1) / d2;
				if (rel < Margin) return Status.Ambiguous;
			}
			return Status.Accepted;
		}

		[TestMethod]
		public void DefaultRejectCost_Is_Tau_Over_One_Minus_Margin()
		{
			var r = Solve(new[] { new Point(0, (0, 0.1)) }, Opt());
			Assert.AreEqual(Tau / (1 - Margin), r.RejectCost, 1e-12);
		}

		[TestMethod]
		public void AB_Example_Both_Bound_Without_Stealing()
		{
			// A: K 0.1, C 0.2; B: K 0.1 (R за порогом). Поточечно A забирает K и B теряется; назначение: A → C, B → K.
			var pts = new[] { new Point(0, (10, 0.1), (11, 0.2)), new Point(1, (10, 0.1), (12, 0.9)) };
			foreach (var reject in new[] { double.NaN, 0.30, 0.60 })
			{
				var r = Solve(pts, Opt(reject));
				Assert.AreEqual(Status.Accepted, Dec(r, 0).Status, $"A, reject={reject}");
				Assert.AreEqual(11, Dec(r, 0).Candidate);
				Assert.AreEqual(Status.Accepted, Dec(r, 1).Status, $"B, reject={reject}");
				Assert.AreEqual(10, Dec(r, 1).Candidate);
				Assert.AreEqual(1, r.Components.Count);
				Assert.AreEqual(0.3, r.Components[0].OptCost, 1e-12);
			}
		}

		[TestMethod]
		public void KBest_Variants_Sorted_And_Start_With_Optimum()
		{
			var pts = new[] { new Point(0, (10, 0.1), (11, 0.2)), new Point(1, (10, 0.1)) };
			var r = Solve(pts, Opt(kBest: 3));
			var v = r.Components[0].Variants;
			Assert.AreEqual(3, v.Count);
			Assert.AreEqual(0.3, v[0].Cost, 1e-12);
			CollectionAssert.AreEqual(new[] { 11, 10 }, v[0].Candidates);
			for (int i = 1; i < v.Count; i++) Assert.IsTrue(v[i].Cost >= v[i - 1].Cost - 1e-12);
			// второй и третий: A → K + B без привязки и A без привязки + B → K, оба по 0.1 + R
			Assert.AreEqual(0.1 + r.RejectCost, v[1].Cost, 1e-12);
			Assert.AreEqual(0.1 + r.RejectCost, v[2].Cost, 1e-12);
		}

		[TestMethod]
		public void Exact_Tie_Is_Ambiguous()
		{
			var pts = new[] { new Point(0, (10, 0.1)), new Point(1, (10, 0.1)) };
			var r = Solve(pts, Opt());
			Assert.AreEqual(Status.Ambiguous, Dec(r, 0).Status);
			Assert.AreEqual(Status.Ambiguous, Dec(r, 1).Status);
			Assert.AreEqual(2, r.Components[0].Points.Count);
		}

		[TestMethod]
		public void Two_Exact_Matches_At_Zero_Are_Ambiguous()
		{
			var r = Solve(new[] { new Point(0, (10, 0.0), (11, 0.0)) }, Opt());
			Assert.AreEqual(Status.Ambiguous, Dec(r, 0).Status);
			Assert.AreEqual(0.0, Dec(r, 0).Ratio, 1e-12);
		}

		[TestMethod]
		public void RejectCost_Changes_Verdict_Under_Near_Tie_Competition()
		{
			// P1: c 0.10; P2: c 0.12, c' 0.29. Кто владеет c — почти ничья.
			var pts = new[] { new Point(0, (10, 0.10)), new Point(1, (10, 0.12), (11, 0.29)) };
			var strict = Solve(pts, Opt(reject: 0.30));          // отказ дёшев: удаление одной из точек — реальная альтернатива
			Assert.AreEqual(Status.Ambiguous, Dec(strict, 0).Status);
			Assert.AreEqual(Status.Ambiguous, Dec(strict, 1).Status);
			var def = Solve(pts, Opt());                          // R = τ/(1−m): удаление дорого, совместная расстановка принимается
			Assert.AreEqual(Status.Accepted, Dec(def, 0).Status);
			Assert.AreEqual(10, Dec(def, 0).Candidate);
			Assert.AreEqual(Status.Accepted, Dec(def, 1).Status);
			Assert.AreEqual(11, Dec(def, 1).Candidate);
		}

		[TestMethod]
		public void Chain_Component_Rearranges_All_Three()
		{
			// Каждая точка ближе к кандидату соседа: A: c2 0.10, c1 0.12; B: c3 0.10, c2 0.12; C: c3 0.12.
			var pts = new[] { new Point(0, (2, 0.10), (1, 0.12)), new Point(1, (3, 0.10), (2, 0.12)), new Point(2, (3, 0.12)) };
			var r = Solve(pts, Opt());
			Assert.AreEqual(1, r.Components.Count);
			Assert.AreEqual(3, r.Components[0].Points.Count);
			Assert.AreEqual(0.36, r.Components[0].OptCost, 1e-12);
			Assert.AreEqual(1, Dec(r, 0).Candidate); Assert.AreEqual(Status.Accepted, Dec(r, 0).Status);
			Assert.AreEqual(2, Dec(r, 1).Candidate); Assert.AreEqual(Status.Accepted, Dec(r, 1).Status);
			Assert.AreEqual(3, Dec(r, 2).Candidate); Assert.AreEqual(Status.Accepted, Dec(r, 2).Status);
		}

		[TestMethod]
		public void Singletons_Equal_Pointwise_Rule_With_Default_Reject()
		{
			var rng = new Random(7);
			int checkedPoints = 0, accepted = 0;
			for (int trial = 0; trial < 4000; trial++)
			{
				var cands = new List<Candidate>();
				int nc = rng.Next(0, 4);
				for (int j = 0; j < nc; j++)
				{
					double d = rng.NextDouble() < 0.15 ? 0.0 : Math.Round(rng.NextDouble() * (rng.NextDouble() < 0.5 ? 0.5 : 1.0), 3);
					cands.Add(new Candidate(100 + j, d));
				}
				var p = new Point { Index = 0, Candidates = cands };
				var r = Solve(new[] { p }, Opt());
				var expected = Pointwise(cands);
				var got = Dec(r, 0).Status;
				Assert.AreEqual(expected, got, "cands: " + string.Join(", ", cands.Select(c => $"{c.Index}:{c.Dist}")));
				if (got == Status.Accepted)
				{
					var nearest = cands.OrderBy(c => c.Dist).ThenBy(c => c.Index).First();
					Assert.AreEqual(nearest.Index, Dec(r, 0).Candidate);
					accepted++;
				}
				checkedPoints++;
			}
			Assert.IsTrue(accepted > 500, $"accepted={accepted} of {checkedPoints}");
		}

		[TestMethod]
		public void Accepted_Implies_Within_Tau_For_Any_Reject()
		{
			var rng = new Random(11);
			foreach (var reject in new[] { 0.30, 0.45, 0.60, 1.0 })
				for (int trial = 0; trial < 300; trial++)
				{
					var pts = RandomComponent(rng, rng.Next(1, 7), 5);
					var r = Solve(pts, Opt(reject: reject, kBest: 0));
					foreach (var d in r.Decisions.Where(d => d.Status == Status.Accepted))
						Assert.IsTrue(d.Dist <= Tau + 1e-12, $"reject={reject}: d={d.Dist}");
				}
		}

		[TestMethod]
		public void Enumeration_And_Hungarian_Agree()
		{
			var rng = new Random(3);
			int comparedAccepted = 0;
			for (int trial = 0; trial < 600; trial++)
			{
				var pts = RandomComponent(rng, rng.Next(2, 9), rng.Next(2, 7));
				foreach (var reject in new[] { double.NaN, 0.30, 0.6 })
				{
					var a = Solve(pts, Opt(reject: reject, enumLimit: 10, kBest: 3));
					var b = Solve(pts, Opt(reject: reject, enumLimit: 0, kBest: 3));
					Assert.AreEqual(a.Components.Count, b.Components.Count);
					for (int c = 0; c < a.Components.Count; c++)
					{
						Assert.AreEqual(a.Components[c].OptCost, b.Components[c].OptCost, 1e-9, $"trial {trial}: opt cost");
						CollectionAssert.AreEqual(a.Components[c].Points, b.Components[c].Points);
						var va = a.Components[c].Variants.Select(v => v.Cost).ToList();
						var vb = b.Components[c].Variants.Select(v => v.Cost).ToList();
						Assert.AreEqual(va.Count, vb.Count, $"trial {trial}: variants count");
						for (int i = 0; i < va.Count; i++) Assert.AreEqual(va[i], vb[i], 1e-9, $"trial {trial}: variant {i}");
					}
					foreach (var da in a.Decisions)
					{
						var db = b.Decisions.Single(x => x.Point == da.Point);
						Assert.AreEqual(da.Status, db.Status, $"trial {trial} point {da.Point} reject {reject}: {da.Reason} | {db.Reason}");
						if (da.Status == Status.Accepted)
						{
							Assert.AreEqual(da.Candidate, db.Candidate);
							Assert.AreEqual(da.Delta, db.Delta, 1e-9);
							comparedAccepted++;
						}
					}
				}
			}
			Assert.IsTrue(comparedAccepted > 500, $"accepted decisions compared: {comparedAccepted}");
		}

		[TestMethod]
		public void Result_Does_Not_Depend_On_Input_Order()
		{
			var rng = new Random(5);
			for (int trial = 0; trial < 100; trial++)
			{
				var pts = RandomComponent(rng, rng.Next(2, 8), rng.Next(2, 6));
				var shuffled = pts.OrderBy(_ => rng.Next()).Select(p => new Point { Index = p.Index, Candidates = p.Candidates.OrderBy(_ => rng.Next()).ToList() }).ToList();
				var a = Solve(pts, Opt());
				var b = Solve(shuffled, Opt());
				foreach (var da in a.Decisions)
				{
					var db = b.Decisions.Single(x => x.Point == da.Point);
					Assert.AreEqual(da.Status, db.Status);
					Assert.AreEqual(da.Candidate, db.Candidate);
					Assert.AreEqual(da.Component, db.Component);
				}
			}
		}

		[TestMethod]
		public void No_Candidates_Lost_And_Beyond_Tau_Lost_Or_Ambiguous()
		{
			var r = Solve(new[] { new Point(0), new Point(1, (10, 0.35)), new Point(2, (11, 0.35), (12, 0.20)) }, Opt());
			Assert.AreEqual(Status.Lost, Dec(r, 0).Status);
			Assert.AreEqual(Status.Lost, Dec(r, 1).Status);         // единственный кандидат за τ, хотя и в пределах R
			Assert.AreEqual(Status.Accepted, Dec(r, 2).Status);     // 0.20 против 0.35: отрыв 0.43
			Assert.AreEqual(12, Dec(r, 2).Candidate);
		}

		[TestMethod]
		public void Component_Ids_Cover_All_Points_And_Sizes_Match()
		{
			var rng = new Random(9);
			var pts = RandomComponent(rng, 12, 6);
			var r = Solve(pts, Opt(kBest: 2));
			Assert.AreEqual(pts.Count, r.Decisions.Count);
			foreach (var comp in r.Components)
				foreach (var pi in comp.Points)
				{
					var d = Dec(r, pi);
					Assert.AreEqual(comp.Id, d.Component);
					Assert.AreEqual(comp.Points.Count, d.ComponentSize);
					if (comp.Variants.Count > 0) Assert.AreEqual(comp.Points.Count, comp.Variants[0].Candidates.Length);
				}
			Assert.IsTrue(r.EnumeratedComponents + r.LapComponents >= 1);
		}

		[TestMethod]
		public void Large_Component_Uses_Hungarian_And_Yields_Sorted_Variants()
		{
			var rng = new Random(13);
			var pts = RandomComponent(rng, 14, 4);   // 14 точек над 4 общими кандидатами — одна компонента > EnumerateLimit
			var r = Solve(pts, Opt(kBest: 4));
			Assert.IsTrue(r.LapComponents >= 1, "expected at least one Hungarian component");
			foreach (var comp in r.Components.Where(c => !c.Enumerated))
			{
				Assert.IsTrue(comp.Variants.Count >= 1);
				Assert.AreEqual(comp.OptCost, comp.Variants[0].Cost, 1e-9);
				for (int i = 1; i < comp.Variants.Count; i++)
				{
					Assert.IsTrue(comp.Variants[i].Cost >= comp.Variants[i - 1].Cost - 1e-12);
					Assert.IsFalse(comp.Variants[i].Candidates.SequenceEqual(comp.Variants[i - 1].Candidates), "duplicate variant");
				}
			}
		}

		[TestMethod]
		public void Exact_Cost_Tie_With_Zero_Distance_Is_Ambiguous_Despite_Rounding()
		{
			// p1: c1 0, c2 0.1985; p2: c1 0.0848, c2 0.2833. Оптимум p1 → c1 + p2 → c2 = 0.2833, альтернатива p1 → c2 + p2 → c1 = 0.2833:
			// ничья в точной арифметике, в double — расхождение ~1e-16; при d = 0 без порога на Δ отношение Δ/(d + Δ) было бы 1.
			var pts = new[] { new Point(0, (1, 0.0), (2, 0.1985)), new Point(1, (1, 0.0848), (2, 0.2833)) };
			foreach (var reject in new[] { double.NaN, 0.6 })
			{
				var r = Solve(pts, Opt(reject));
				Assert.AreEqual(Status.Ambiguous, Dec(r, 0).Status, Dec(r, 0).Reason);
				Assert.AreEqual(Status.Ambiguous, Dec(r, 1).Status, Dec(r, 1).Reason);
			}
		}

		/// <summary>Случайная компонента: n точек, кандидаты из общего пула размера pool (индексы 100..), 1–3 кандидата на точку.</summary>
		private static List<Point> RandomComponent(Random rng, int n, int pool)
		{
			var pts = new List<Point>();
			for (int i = 0; i < n; i++)
			{
				var p = new Point { Index = i };
				int nc = rng.Next(1, 4);
				var chosen = new HashSet<int>();
				for (int j = 0; j < nc; j++)
				{
					int c = 100 + rng.Next(pool);
					if (!chosen.Add(c)) continue;
					double d = rng.NextDouble() < 0.1 ? 0.0 : Math.Round(rng.NextDouble() * 0.7, 3);
					p.Candidates.Add(new Candidate(c, d));
				}
				pts.Add(p);
			}
			return pts;
		}
	}
}
