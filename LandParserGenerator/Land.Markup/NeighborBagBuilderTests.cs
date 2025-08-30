using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Land.Markup;   // NeighborBagBuilder, MethodAnchor
using VPTree;        // Dist.NeighborSigKey

namespace Land.Markup
{
	[TestClass]
	public class NeighborBagBuilderTests
	{
		// ---- helpers -------------------------------------------------------

		// Вес как в билдере: w = 1 / (1 + d)
		private static double W(int d) => 1.0 / (1.0 + d);

		private static Tuple<string, string> Arg(string type, string name) => Tuple.Create(type, name);

		private static MethodAnchor A(string id, string name, Tuple<string, string>[] args, string[] rets, string parent)
		    => MethodAnchor.FromRaw(id, 0, 100, name, args ?? new Tuple<string, string>[0], rets ?? new string[0], parent);

		private static void AssertBagsAreEqual(
		    IDictionary<string, double> actual,
		    IDictionary<string, double> expected,
		    double eps = 1e-12)
		{
			actual = actual ?? new Dictionary<string, double>();
			expected = expected ?? new Dictionary<string, double>();

			Assert.AreEqual(expected.Count, actual.Count, "Different number of neighbor entries.");

			foreach (var kv in expected)
			{
				Assert.IsTrue(actual.ContainsKey(kv.Key), $"Missing neighbor key: {kv.Key}");
				Assert.AreEqual(kv.Value, actual[kv.Key], eps, $"Different weight for key {kv.Key}");
			}
		}

		// ---- tests ---------------------------------------------------------

		[TestMethod]
		[TestCategory("NeighborBags")]
		public void Build_Window1_CenterHasTwoNeighbors_WithWeightHalf()
		{
			// N1, X, N2  (parent одинаковый)
			var N1 = A("N#1", "Left", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var X = A("X#", "Center", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var N2 = A("N#2", "Right", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");

			var list = new List<MethodAnchor> { N1, X, N2 };

			NeighborBagBuilder.BuildBagsInOrder(list, window: 1);

			var k1 = Dist.NeighborSigKey(N1);
			var k2 = Dist.NeighborSigKey(N2);

			var expected = new Dictionary<string, double>(StringComparer.Ordinal)
			    {
				{ k1, W(1) }, // 0.5
				{ k2, W(1) }, // 0.5
			    };

			AssertBagsAreEqual(X.NeighborBag, expected);
		}

		[TestMethod]
		[TestCategory("NeighborBags")]
		public void Build_Window2_WeightsDecay_HalfAndThird()
		{
			// N1, N2, X, N3, N4  (окно=2 → d=1 вес 0.5, d=2 вес ≈ 0.3333)
			var N1 = A("N#1", "A", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var N2 = A("N#2", "B", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");
			var X = A("X#", "X", new[] { Arg("bool", "b") }, new[] { "void" }, "Svc");
			var N3 = A("N#3", "C", new[] { Arg("[]byte", "buf") }, new[] { "void" }, "Svc");
			var N4 = A("N#4", "D", new[] { Arg("float64", "f") }, new[] { "void" }, "Svc");

			var list = new List<MethodAnchor> { N1, N2, X, N3, N4 };

			NeighborBagBuilder.BuildBagsInOrder(list, window: 2);

			var expected = new Dictionary<string, double>(StringComparer.Ordinal)
			    {
				{ Dist.NeighborSigKey(N2), W(1) }, // 0.5
				{ Dist.NeighborSigKey(N3), W(1) }, // 0.5
				{ Dist.NeighborSigKey(N1), W(2) }, // ≈ 0.3333
				{ Dist.NeighborSigKey(N4), W(2) }, // ≈ 0.3333
			    };

			AssertBagsAreEqual(X.NeighborBag, expected);
		}

		[TestMethod]
		[TestCategory("NeighborBags")]
		public void RenameNeighbor_DoesNotChangeBag_WhenSignatureSame()
		{
			// Имя соседа меняется, сигнатура та же → ключ Dist.NeighborSigKey не меняется
			var N1 = A("N#1", "Neighbor", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var N1_ren = A("N#1r", "NeighborRenamed", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var X1 = A("X1", "X", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var X2 = A("X2", "X", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var N2 = A("N#2", "Right", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");

			var list1 = new List<MethodAnchor> { N1, X1, N2 };
			var list2 = new List<MethodAnchor> { N1_ren, X2, N2 };

			NeighborBagBuilder.BuildBagsInOrder(list1, window: 1);
			NeighborBagBuilder.BuildBagsInOrder(list2, window: 1);

			AssertBagsAreEqual(X1.NeighborBag, X2.NeighborBag);
		}

		[TestMethod]
		[TestCategory("NeighborBags")]
		public void Build_ClearsExistingBag_Content()
		{
			var L = A("L", "Left", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var X = A("X", "X", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var R = A("R", "Right", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");

			// Предзаполним мусором
			X.NeighborBag = new Dictionary<string, double>(StringComparer.Ordinal)
			    {
				{ "bogus", 42.0 }
			    };

			var list = new List<MethodAnchor> { L, X, R };
			NeighborBagBuilder.BuildBagsInOrder(list, window: 1);

			var expected = new Dictionary<string, double>(StringComparer.Ordinal)
			    {
				{ Dist.NeighborSigKey(L), W(1) }, // 0.5
				{ Dist.NeighborSigKey(R), W(1) }, // 0.5
			    };

			AssertBagsAreEqual(X.NeighborBag, expected);
			Assert.IsFalse(X.NeighborBag.ContainsKey("bogus"), "Old content must be cleared.");
		}

		[TestMethod]
		[TestCategory("NeighborBags")]
		public void WindowLongerThanList_IsSafe_AndUsesExistingNeighborsOnly()
		{
			var A1 = A("A1", "A", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var A2 = A("A2", "B", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");

			var list = new List<MethodAnchor> { A1, A2 };

			NeighborBagBuilder.BuildBagsInOrder(list, window: 10);

			// У каждого по одному соседу на расстоянии 1 → вес 0.5 (W(1))
			var e1 = new Dictionary<string, double>(StringComparer.Ordinal)
			    {
				{ Dist.NeighborSigKey(A2), W(1) }
			    };
			var e2 = new Dictionary<string, double>(StringComparer.Ordinal)
			    {
				{ Dist.NeighborSigKey(A1), W(1) }
			    };

			AssertBagsAreEqual(A1.NeighborBag, e1);
			AssertBagsAreEqual(A2.NeighborBag, e2);
		}

		[TestMethod]
		[TestCategory("NeighborBags")]
		public void NullOrEmptyList_IsNoop()
		{
			// Должно отрабатывать без исключений и без побочных эффектов
			NeighborBagBuilder.BuildBagsInOrder(null, window: 3);
			NeighborBagBuilder.BuildBagsInOrder(new List<MethodAnchor>(), window: 3);
		}
	}
}
