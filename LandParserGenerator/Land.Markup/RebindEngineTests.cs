using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using VPTree;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace Land.Markup
{
	[TestClass]
	public class RebindEngineTests
	{
		private List<MethodAnchor> _old;
		private Rebinder _rebinder;
		private Dist.Weights _w;

		[TestInitialize]
		public void Setup()
		{
			_old = new List<MethodAnchor>();

			// Старые якоря (эталон)
			_old.Add(MethodAnchor.FromRaw(
			    "old#1", 0, 100, "ComputeHash",
			    new[] { Tuple.Create("int", "a"), Tuple.Create("string", "b") },
			    new[] { "int" },
			    "Hasher"));

			_old.Add(MethodAnchor.FromRaw(
			    "old#2", 0, 100, "FindUser",
			    new[] { Tuple.Create("int", "id") },
			    new[] { "*User", "error" },
			    "Repo"));

			_old.Add(MethodAnchor.FromRaw(
			    "old#3", 0, 100, "WriteJSON",
			    new[] { Tuple.Create("io.Writer", "w"), Tuple.Create("any", "v"), Tuple.Create("bool", "indent") },
			    new[] { "error" },
			    "Encoder"));

			// Дополнительно — пара для теста one-to-one конфликта
			_old.Add(MethodAnchor.FromRaw(
			    "old#A", 0, 100, "Sum",
			    new[] { Tuple.Create("int", "a"), Tuple.Create("int", "b") },
			    new[] { "int" },
			    "MathSvc"));

			_old.Add(MethodAnchor.FromRaw(           // очень близкий к old#A (разница только в имени)
			    "old#B", 0, 100, "Sum2",
			    new[] { Tuple.Create("int", "a"), Tuple.Create("int", "b") },
			    new[] { "int" },
			    "MathSvc"));

			// Двойники для неоднозначности (оба абсолютно одинаковые)
			_old.Add(MethodAnchor.FromRaw(
			    "old#D1", 0, 100, "Foo",
			    new[] { Tuple.Create("int", "x") },
			    new[] { "int" },
			    "Svc"));

			_old.Add(MethodAnchor.FromRaw(
			    "old#D2", 0, 100, "Foo",
			    new[] { Tuple.Create("int", "x") },
			    new[] { "int" },
			    "Svc"));

			_w = new Dist.Weights();
			_rebinder = new Rebinder(_old, _w); // VPTree со стабильным seed внутри
		}

		private static MethodAnchor NA(string id, string name, Tuple<string, string>[] args, string[] rets, string recv)
		{
			return MethodAnchor.FromRaw(id, 0, 100, name, args, rets, recv);
		}

		private static MatchResult FindByNewId(List<MatchResult> list, string newId)
		{
			foreach (var m in list)
				if (m.New != null && m.New.Id == newId) return m;
			return null;
		}

		[TestMethod]
		public void Rebind_Accepts_Rename_SameSignature()
		{
			// Небольшое переименование (1 символ), сигнатура/receiver прежние
			var newAnchors = new List<MethodAnchor>
			    {
				NA("new#1", "ComputeHsh", // пропущена 'a' в Hash
				   new[] { Tuple.Create("int","a"), Tuple.Create("string","b") },
				   new[] { "int" }, "Hasher")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);
			var res = engine.Rebind(newAnchors, k: 3, tau: 0.25, margin: 0.02);

			var m = FindByNewId(res, "new#1");
			Assert.IsNotNull(m, "Result for new#1 not found");
			Assert.AreEqual(MatchStatus.Accepted, m.Status, "Rename should be accepted");
			Assert.IsNotNull(m.Old);
			Assert.AreEqual("old#1", m.Old.Id, "Should map to old#1 ComputeHash");
			Assert.IsTrue(m.BestDist < 0.25, "Distance should be under tau");
		}

		[TestMethod]
		public void Rebind_Accepts_ArgOrderChange()
		{
			// Перестановка аргументов — должен остаться тот же метод (венгерка=0)
			var newAnchors = new List<MethodAnchor>
			    {
				NA("new#2", "WriteJSON",
				   new[] {
				       Tuple.Create("any","v"),
				       Tuple.Create("io.Writer","w"),
				       Tuple.Create("bool","indent")
				   },
				   new[] { "error" }, "Encoder")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);
			var res = engine.Rebind(newAnchors, k: 3, tau: 0.20, margin: 0.02);

			var m = FindByNewId(res, "new#2");
			Assert.IsNotNull(m);
			Assert.AreEqual(MatchStatus.Accepted, m.Status, "Arg reorder should be accepted");
			Assert.IsNotNull(m.Old);
			Assert.AreEqual("old#3", m.Old.Id, "Should map to old#3 WriteJSON");
			Assert.IsTrue(m.BestDist <= 0.20);
		}

		[TestMethod]
		public void Rebind_Ambiguous_When_Two_Identical_Olds()
		{
			// Новый полностью совпадает с двумя старыми (old#D1, old#D2) -> два кандидата с dist=0 => Ambiguous
			var newAnchors = new List<MethodAnchor>
			    {
				NA("new#3", "Foo",
				   new[] { Tuple.Create("int","x") },
				   new[] { "int" }, "Svc")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);
			// tau достаточно большой, чтобы считалось «похоже», margin > 0 => разницы между топ-1 и топ-2 нет
			var res = engine.Rebind(newAnchors, k: 3, tau: 0.5, margin: 0.05);

			var m = FindByNewId(res, "new#3");
			Assert.IsNotNull(m);
			Assert.AreEqual(MatchStatus.Ambiguous, m.Status, "Two perfect matches must be ambiguous");
			Assert.IsTrue(m.Candidates.Count >= 2, "Expect at least two candidates");
			// обе дистанции должны быть равны
			double d1 = m.Candidates[0].Dist;
			double d2 = m.Candidates[1].Dist;
			Assert.AreEqual(d1, d2, 1e-9, "Top-2 distances must be equal");
		}

		[TestMethod]
		public void Rebind_NoMatch_When_TooFar()
		{
			// Сильно другой метод — по всем признакам далеко
			var newAnchors = new List<MethodAnchor>
			    {
				NA("new#4", "Other",
				   new[] { Tuple.Create("string","s") },
				   new[] { "string" }, "OtherSvc")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);
			var res = engine.Rebind(newAnchors, k: 3, tau: 0.20, margin: 0.02);

			var m = FindByNewId(res, "new#4");
			Assert.IsNotNull(m);
			Assert.AreEqual(MatchStatus.NoMatch, m.Status, "Far method should be NoMatch under strict tau");
			Assert.IsTrue(m.BestDist > 0.20, "Best distance should exceed tau");
		}

		[TestMethod]
		public void Rebind_Resolves_OneToOne_Conflict_With_Alternative()
		{
			// Два НОВЫХ, оба хотят old#A (Sum), второй может уйти на old#B (Sum2) в пределах tau
			var newAnchors = new List<MethodAnchor>
			    {
				// Полный дубль old#A
				NA("new#5", "Sum",
				   new[] { Tuple.Create("int","a"), Tuple.Create("int","b") },
				   new[] { "int" }, "MathSvc"),

				// Тоже дубль old#A (т.е. bestDist=0 к old#A),
				// а ко второму кандидату old#B расстояние чуть больше (разница в имени Sum vs Sum2)
				NA("new#6", "Sum",
				   new[] { Tuple.Create("int","a"), Tuple.Create("int","b") },
				   new[] { "int" }, "MathSvc")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);
			// tau достаточно большой, чтобы и secondDist попадал под порог,
			// margin маленький, чтобы оба изначально считались confident proposals.
			var res = engine.Rebind(newAnchors, k: 3, tau: 0.5, margin: 0.02);

			var m1 = FindByNewId(res, "new#5");
			var m2 = FindByNewId(res, "new#6");
			Assert.IsNotNull(m1);
			Assert.IsNotNull(m2);
			Assert.AreEqual(MatchStatus.Accepted, m1.Status);
			Assert.AreEqual(MatchStatus.Accepted, m2.Status);

			// Должны разойтись на разные old
			Assert.IsNotNull(m1.Old);
			Assert.IsNotNull(m2.Old);
			Assert.AreNotEqual(m1.Old.Id, m2.Old.Id, "One-to-one constraint must be enforced");

			// Один из них к old#A, второй к old#B
			var pair = new HashSet<string> { m1.Old.Id, m2.Old.Id };
			Assert.IsTrue(pair.Contains("old#A") && pair.Contains("old#B"),
			    "Expected assignments to old#A and old#B");
		}

		[TestMethod]
		public void Rebind_Candidates_List_Is_Sorted_By_Distance()
		{
			// Контрольная проверка сортировки кандидатов по возрастанию дистанции
			var newAnchors = new List<MethodAnchor>
			    {
				 NA("new#7", "ComputeHsh", // одно отличие в имени, чтобы не сработал ExactHash
				   new[] { Tuple.Create("int","a"), Tuple.Create("string","b") },
				   new[] { "int" }, "Hasher")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);
			var res = engine.Rebind(newAnchors, k: 3, tau: 0.5, margin: 0.01);

			var m = FindByNewId(res, "new#7");
			Assert.IsNotNull(m);
			Assert.IsTrue(m.Candidates.Count >= 2);

			// Проверим монотонность dist
			for (int i = 1; i < m.Candidates.Count; i++)
			{
				Assert.IsTrue(m.Candidates[i - 1].Dist <= m.Candidates[i].Dist + 1e-12,
				    "Candidates must be sorted by ascending distance");
			}
		}

		[TestMethod]
		public void Rebind_ExactMatch_ShouldSkipKnn_AndHaveNoCandidates()
		{
			// Берём якорь, который ТОЧНО совпадает с одним из _old (как в Setup: ComputeHash)
			var newAnchors = new List<MethodAnchor>
			    {
				NA("new_exact", "ComputeHash",
				    new[] { Tuple.Create("int","a"), Tuple.Create("string","b") },
				    new[] { "int" }, "Hasher")
			    };

			var engine = new RebindEngine(_rebinder, _old, _w);

			var res = engine.Rebind(newAnchors, k: 3, tau: 0.5, margin: 0.01);

			// Проверяем: точный матч принят на пред-фильтре и kNN не трогался
			var m = res.Single(r => r.New.Id == "new_exact");
			Assert.AreEqual(MatchStatus.Accepted, m.Status, "Exact совпадение должно приниматься без kNN");
			Assert.IsNotNull(m.Old, "Должен быть выбран старый якорь");
			Assert.AreEqual(0.0, m.BestDist, 1e-12, "BestDist для точного совпадения — 0");
			Assert.AreEqual(0, m.Candidates.Count, "kNN не должен вызываться — список кандидатов пуст");
		}
	}
}
