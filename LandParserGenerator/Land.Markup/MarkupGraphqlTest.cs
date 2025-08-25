using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace Land.Markup
{
	[TestClass]
	public class MarkupGraphqlTest
	{
		// веса, изолирующие компонент "соседи"
		private static Dist.Weights WNeigh(double neighW = 1.0) => new Dist.Weights
		{
			NameW = 0,
			ArgsW = 0,
			ReturnsW = 0,
			ParentW = 0,
			NeighW = neighW
		};

		// чтобы короче вызывать настоящий билдер
		private static void BuildBags(List<MethodAnchor> ordered, int window = 1)
		    => NeighborBagBuilder.BuildBagsInOrder(ordered, window);

		private static Tuple<string, string> Arg(string type, string name) => Tuple.Create(type, name);

		private static MethodAnchor A(
		    string id, string name,
		    Tuple<string, string>[] args,
		    string[] rets,
		    string recv)
		{
			return MethodAnchor.FromRaw(id, name, args ?? new Tuple<string, string>[0], rets ?? new string[0], recv);
		}

		#region 6) Neighbors — учёт контекста соседей в kNN/VPTree

		[TestMethod]
		[TestCategory("KNN/Neighbors")]
		public void Nearest_ByNeighbors_PrefersMatchingContext()
		{
			// Корпус в одном parent'е "Svc": у Foo1 соседи N1/N2, у Foo2 — M1/M2.
			var N1 = A("N#1", "ComputeHash",
				   new[] { Arg("int", "a"), Arg("string", "b") },
				   new[] { "int" }, "Svc");
			var N2 = A("N#2", "WriteJSON",
				   new[] { Arg("io.Writer", "w"), Arg("any", "v"), Arg("bool", "indent") },
				   new[] { "error" }, "Svc");
			var M1 = A("M#1", "Ping",
				   new[] { Arg("string", "s") },
				   new[] { "bool" }, "Svc");
			var M2 = A("M#2", "Save",
				   new[] { Arg("[]byte", "buf") },
				   new[] { "error" }, "Svc");

			var Foo1 = A("F#1", "Foo", new[] { Arg("int", "x") }, new[] { "int" }, "Svc");
			var Foo2 = A("F#2", "Foo", new[] { Arg("int", "x") }, new[] { "int" }, "Svc");

			// Порядок: N1, Foo1, N2, M1, Foo2, M2 → строим мешки
			var corpusOrdered = new List<MethodAnchor> { N1, Foo1, N2, M1, Foo2, M2 };
			BuildBags(corpusOrdered, window: 1);

			var corpus = corpusOrdered.ToList();
			var rebinder = new Rebinder(corpus, WNeigh(1.0));

			// Запрос q с соседями N1 и N2 (как у Foo1)
			var q = A("Q#neigh", "Foo", new[] { Arg("int", "x") }, new[] { "int" }, "Svc");
			var qOrdered = new List<MethodAnchor> { N1, q, N2 };
			BuildBags(qOrdered, window: 1);

			var knn = rebinder.Query(q, 3);
			var best = corpus[knn[0].Index];

			Assert.AreEqual("F#1", best.Id, "Ближайшим должен быть Foo1 благодаря совпадению соседского контекста.");
			for (int i = 1; i < knn.Count; i++) Assert.IsTrue(knn[i - 1].Dist <= knn[i].Dist);
		}

		[TestMethod]
		[TestCategory("KNN/Neighbors")]
		public void Neighbors_Rename_DoesNotAffect_WhenSignatureSame()
		{
			// Имя соседа поменяли, но сигнатура та же — ключ Dist.NeighborSigKey одинаковый.
			var N1 = A("N#1", "Neighbor", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var N1_ren = A("N#1r", "NeighborRenamed", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var N2 = A("N#2", "Right", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");

			var Foo1 = A("F#1", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var Foo2 = A("F#2", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");

			var corpusOrdered = new List<MethodAnchor> { N1, Foo1, N2, N1, Foo2, N2 };
			BuildBags(corpusOrdered, window: 1);
			var corpus = corpusOrdered.ToList();

			var rebinder = new Rebinder(corpus, WNeigh(1.0));

			// q1 с N1, q2 с N1_ren
			var q1 = A("Q#1", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var q2 = A("Q#2", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			BuildBags(new List<MethodAnchor> { N1, q1, N2 }, window: 1);
			BuildBags(new List<MethodAnchor> { N1_ren, q2, N2 }, window: 1);

			var knn1 = rebinder.Query(q1, 2);
			var knn2 = rebinder.Query(q2, 2);

			Assert.AreEqual("F#1", corpus[knn1[0].Index].Id);
			Assert.AreEqual("F#1", corpus[knn2[0].Index].Id);
			Assert.AreEqual(knn1[0].Dist, knn2[0].Dist, 1e-12, "Переименование соседа не должно менять дистанцию.");
		}

		[TestMethod]
		[TestCategory("KNN/Neighbors")]
		public void Neighbors_Distinguish_Twins_WithSameSignature()
		{
			// Корпус: две одинаковые по сигнатуре Foo, различаются только окружением.
			var L = A("L#", "Left", new[] { Arg("bool", "a") }, new[] { "void" }, "Svc");
			var R1 = A("R1#", "Right", new[] { Arg("int", "b") }, new[] { "void" }, "Svc");
			var R2 = A("R2#", "Rght2", new[] { Arg("[]byte", "buf") }, new[] { "void" }, "Svc");

			var F1 = A("F#1", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var F2 = A("F#2", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");

			// Проверим, что ключи соседей действительно разные
			Assert.AreNotEqual(Dist.NeighborSigKey(R1), Dist.NeighborSigKey(R2));

			// Порядок: L, F1, R1, R2, F2, L  → у F1 соседи L/R1, у F2 — R2/L
			var corpusOrdered = new List<MethodAnchor> { L, F1, R1, R2, F2, L };

			// Строим мешки ДО создания ребиндера
			NeighborBagBuilder.BuildBagsInOrder(corpusOrdered, window: 1);
			var corpus = corpusOrdered.ToList();

			// Запрос с соседями L и R1 → должен тянуть к F1
			var q = A("Q#", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			NeighborBagBuilder.BuildBagsInOrder(new List<MethodAnchor> { L, q, R1 }, window: 1);

			var w = new Dist.Weights { NameW = 0, ArgsW = 0, ReturnsW = 0, ParentW = 0, NeighW = 1.0 };
			var rebinder = new Rebinder(corpus, w);

			// 1) Проверяем, что лучший сосед — F#1 (контекст соседей сработал)
			var knn = rebinder.Query(q, 3);
			Assert.IsTrue(knn.Count >= 1, "Ожидался хотя бы один сосед.");
			var best = corpus[knn[0].Index];
			Assert.AreEqual("F#1", best.Id, "Ближайшим должен быть F#1 благодаря совпадению соседского контекста.");

			// 2) Независимо от топ-k: прямая метрика отдаёт d(q,F1) < d(q,F2)
			var dF1 = Dist.AnchorDistance(q, F1, w);
			var dF2 = Dist.AnchorDistance(q, F2, w);
			Console.WriteLine($"d(F#1)={dF1:0.####}, d(F#2)={dF2:0.####}");
			Assert.IsTrue(dF1 < dF2, "Дистанция до F#1 должна быть строго меньше, чем до F#2.");
		}


		[TestMethod]
		[TestCategory("KNN/Neighbors")]
		public void Neighbors_WeightZero_MakesNeighborsIrrelevant()
		{
			// Те же данные, но NeighW=0 → влияние соседей выключено, расстояния к F1 и F2 равны.
			var L = A("L#", "Left", new[] { Arg("int", "a") }, new[] { "void" }, "Svc");
			var R1 = A("R1#", "Right", new[] { Arg("int", "b") }, new[] { "void" }, "Svc");
			var R2 = A("R2#", "Right2", new[] { Arg("string", "s") }, new[] { "void" }, "Svc");

			var F1 = A("F#1", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			var F2 = A("F#2", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");

			var corpusOrdered = new List<MethodAnchor> { L, F1, R1, R2, F2, L };
			BuildBags(corpusOrdered, window: 1);
			var corpus = corpusOrdered.ToList();

			var rebinder = new Rebinder(corpus, WNeigh(0.0)); // соседи не влияют

			var q = A("Q#", "Foo", new[] { Arg("int", "x") }, new[] { "void" }, "Svc");
			BuildBags(new List<MethodAnchor> { L, q, R1 }, window: 1);

			var knn = rebinder.Query(q, 2);
			// Из-за нулевых весов все компоненты равны → дистанции к F1 и F2 должны совпадать
			Assert.AreEqual(knn[0].Dist, knn[1].Dist, 1e-12, "При NeighW=0 соседский контекст не должен влиять на дистанции.");
		}

		#endregion
	}
}
