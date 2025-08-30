using System;
using System.Collections.Generic;
using System.Linq;
using Land.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;


namespace Land.Markup
{
	[TestClass]
	public class ExactHashRebinderTests
	{
		// Удобный хелпер
		private static MethodAnchor NA(string id, string name,
		    Tuple<string, string>[] args, string[] rets, string recv)
		{
			return MethodAnchor.FromRaw(id, 0, 100, name, args, rets, recv);
		}

		[TestMethod]
		public void ExactMatch_Should_Be_Accepted()
		{
			var old = new List<MethodAnchor>
			    {
				NA("old#1", "ComputeHash",
				   new[] { Tuple.Create("int","a"), Tuple.Create("string","b") },
				   new[] { "int" }, "Hasher")
			    };

			var rebinder = new ExactHashRebinder(old);

			var fresh = new List<MethodAnchor>
			    {
				NA("new#1", "ComputeHash",
				   new[] { Tuple.Create("int","a"), Tuple.Create("string","b") },
				   new[] { "int" }, "Hasher")
			    };

			var res = rebinder.RebindExact(fresh);
			Assert.AreEqual(1, res.Count);
			var m = res[0];
			Assert.AreEqual(MatchStatus.Accepted, m.Status);
			Assert.IsNotNull(m.Old);
			Assert.AreEqual("old#1", m.Old.Id);
			Assert.AreEqual(0.0, m.BestDist, 1e-12);
		}

		[TestMethod]
		public void FullRename_SameSignature_Should_Be_NoMatch_For_Exact()
		{
			var old = new List<MethodAnchor>
			    {
				NA("old#1", "FindUser",
				   new[] { Tuple.Create("int","id") },
				   new[] { "*User","error" }, "Repo")
			    };

			var rebinder = new ExactHashRebinder(old);

			// Полный rename, сигнатура идентична
			var fresh = new List<MethodAnchor>
			    {
				NA("new#1", "LoadProfile",
				   new[] { Tuple.Create("int","id") },
				   new[] { "*User","error" }, "Repo")
			    };

			var res = rebinder.RebindExact(fresh);
			Assert.AreEqual(1, res.Count);
			Assert.AreEqual(MatchStatus.NoMatch, res[0].Status,
			    "Exact-хеш не должен матчить полное переименование");
		}

		[TestMethod]
		public void ArgOrderChange_Should_Be_NoMatch_For_Exact()
		{
			var old = new List<MethodAnchor>
			    {
				NA("old#1", "WriteJSON",
				   new[] {
				       Tuple.Create("io.Writer","w"),
				       Tuple.Create("any","v"),
				       Tuple.Create("bool","indent")
				   },
				   new[] { "error" }, "Encoder")
			    };

			var rebinder = new ExactHashRebinder(old);

			// Поменяли порядок аргументов
			var fresh = new List<MethodAnchor>
			    {
				NA("new#1", "WriteJSON",
				   new[] {
				       Tuple.Create("any","v"),
				       Tuple.Create("io.Writer","w"),
				       Tuple.Create("bool","indent")
				   },
				   new[] { "error" }, "Encoder")
			    };

			var res = rebinder.RebindExact(fresh);
			Assert.AreEqual(1, res.Count);
			Assert.AreEqual(MatchStatus.NoMatch, res[0].Status,
			    "Exact-хеш учитывает порядок аргументов, тут должно быть NoMatch");
		}

		[TestMethod]
		public void DuplicatedOlds_WithSameExactKey_Should_Be_Ambiguous()
		{
			// Два старых якоря с одинаковой каноникой (полные дубликаты)
			var old = new List<MethodAnchor>
			    {
				NA("old#D1", "Foo",
				   new[] { Tuple.Create("int","x") },
				   new[] { "int" }, "Svc"),
				NA("old#D2", "Foo",
				   new[] { Tuple.Create("int","x") },
				   new[] { "int" }, "Svc"),
			    };

			var rebinder = new ExactHashRebinder(old);

			var fresh = new List<MethodAnchor>
			    {
				NA("new#1", "Foo",
				   new[] { Tuple.Create("int","x") },
				   new[] { "int" }, "Svc"),
			    };

			var res = rebinder.RebindExact(fresh);
			Assert.AreEqual(1, res.Count);
			var m = res[0];
			Assert.AreEqual(MatchStatus.Ambiguous, m.Status,
			    "При нескольких старых по одному ключу должно быть Ambiguous");
			Assert.IsTrue(m.Candidates.Count >= 2, "Ожидались как минимум два кандидата");
			var ids = new HashSet<string>(m.Candidates.Select(c => c.Old.Id));
			CollectionAssert.AreEquivalent(new[] { "old#D1", "old#D2" }, ids.ToArray());
		}

		[TestMethod]
		public void OneToOne_TwoNewToOneOld_Second_Should_Be_Ambiguous()
		{
			var old = new List<MethodAnchor>
			    {
				NA("old#1", "Sum",
				   new[] { Tuple.Create("int","a"), Tuple.Create("int","b") },
				   new[] { "int" }, "MathSvc")
			    };

			var rebinder = new ExactHashRebinder(old);

			// Два новых, полностью совпадающих с одним и тем же old
			var fresh = new List<MethodAnchor>
			    {
				NA("new#1", "Sum",
				   new[] { Tuple.Create("int","a"), Tuple.Create("int","b") },
				   new[] { "int" }, "MathSvc"),
				NA("new#2", "Sum",
				   new[] { Tuple.Create("int","a"), Tuple.Create("int","b") },
				   new[] { "int" }, "MathSvc"),
			    };

			var res = rebinder.RebindExact(fresh);
			Assert.AreEqual(2, res.Count);

			var r1 = res.First(r => r.New.Id == "new#1");
			var r2 = res.First(r => r.New.Id == "new#2");

			// Первый займёт old#1
			Assert.AreEqual(MatchStatus.Accepted, r1.Status);
			Assert.IsNotNull(r1.Old);
			Assert.AreEqual("old#1", r1.Old.Id);

			// Второй уже не может занять того же — должен быть Ambiguous/NoMatch (в нашей реализации — Ambiguous)
			Assert.AreNotEqual(MatchStatus.Accepted, r2.Status);
			Assert.AreEqual(MatchStatus.NoMatch, r2.Status);
		}

		[TestMethod]
		public void DifferentReceiverOrReturns_Should_Not_Match()
		{
			var old = new List<MethodAnchor>
			    {
				NA("old#1", "Do",
				   new[] { Tuple.Create("int","x") },
				   new[] { "bool" }, "SvcA")
			    };

			var rebinder = new ExactHashRebinder(old);

			// Отличается receiver
			var fresh1 = new List<MethodAnchor>
			    {
				NA("new#1", "Do",
				   new[] { Tuple.Create("int","x") },
				   new[] { "bool" }, "SvcB")
			    };
			var r1 = rebinder.RebindExact(fresh1)[0];
			Assert.AreEqual(MatchStatus.NoMatch, r1.Status);

			// Отличается возвращаемый тип
			var fresh2 = new List<MethodAnchor>
			    {
				NA("new#2", "Do",
				   new[] { Tuple.Create("int","x") },
				   new[] { "error" }, "SvcA")
			    };
			var r2 = rebinder.RebindExact(fresh2)[0];
			Assert.AreEqual(MatchStatus.NoMatch, r2.Status);
		}
	}
}
