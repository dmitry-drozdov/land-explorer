using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;

namespace Tests
{
	/// <summary>
	/// Аксиомы (псевдо)метрики для Dist.AnchorDistance и её компонент на РЕАЛЬНОМ корпусе.
	/// Ключевой дефект (аудит 2026-08, §3.1): ArgsDistance с нормировкой на переменный
	/// n = max(|A|,|B|) нарушает неравенство треугольника. Режим Constant это устраняет.
	/// </summary>
	[TestClass]
	public class MetricAxiomTests
	{
		private const double Eps = 1e-12;

		internal static string CorpusPath => Path.Combine(AppContext.BaseDirectory, "TestData", "anchors_object.json");

		internal static List<MethodAnchor> LoadCorpus()
		{
			Assert.IsTrue(File.Exists(CorpusPath), "Нет TestData/anchors_object.json");
			var list = AnchorsIO.LoadJson(CorpusPath);
			Assert.IsTrue(list.Count > 1000, "Корпус подозрительно мал");
			return list;
		}

		private static MethodAnchor.Arg Arg(string type, string name) => new MethodAnchor.Arg { TypeNorm = type, NameNorm = name };

		private static Dist.Weights W(Dist.ArgsNormalization mode) => new Dist.Weights { ArgsNorm = mode };

		private static bool Violates(double dab, double dbc, double dac) => dac > dab + dbc + Eps;

		[TestMethod]
		[TestCategory("Metric/Args")]
		public void ArgsDistance_AuditCounterexample_LegacyViolates_ConstantHolds()
		{
			// A = (x), B = (x, y), C = (y) — реалистичные Go-аргументы из аудита.
			var x = Arg("map[string]interface{}", "payload");
			var y = Arg("int32", "ctx");
			var A = new List<MethodAnchor.Arg> { x };
			var B = new List<MethodAnchor.Arg> { x, y };
			var C = new List<MethodAnchor.Arg> { y };

			var legacy = W(Dist.ArgsNormalization.Legacy);
			double lab = Dist.ArgsDistance(A, B, legacy), lbc = Dist.ArgsDistance(B, C, legacy), lac = Dist.ArgsDistance(A, C, legacy);
			Assert.IsTrue(Violates(lab, lbc, lac), $"Legacy должен нарушать: d(A,C)={lac:F4} > {lab:F4}+{lbc:F4}");
			Assert.AreEqual(0.8896, lac, 1e-3, "Число из аудита (0.8896) должно воспроизводиться");

			var constant = W(Dist.ArgsNormalization.Constant);
			double cab = Dist.ArgsDistance(A, B, constant), cbc = Dist.ArgsDistance(B, C, constant), cac = Dist.ArgsDistance(A, C, constant);
			Assert.IsFalse(Violates(cab, cbc, cac), $"Constant должен выполнять: d(A,C)={cac:F4} <= {cab:F4}+{cbc:F4}");
			Assert.IsTrue(cac >= 0 && cac <= 1.0);
		}

		[TestMethod]
		[TestCategory("Metric/Args")]
		public void ArgsDistance_StructuredTriplesFromRealArgs_ConstantNeverViolates()
		{
			var corpus = LoadCorpus();
			var args = corpus.Where(a => a.Args != null).SelectMany(a => a.Args)
				.GroupBy(a => a.TypeNorm + "" + a.NameNorm)
				.Select(g => g.First())
				.OrderBy(a => a.TypeNorm + "" + a.NameNorm, StringComparer.Ordinal)
				.ToList();
			Assert.IsTrue(args.Count > 100, "Мало различных аргументов в корпусе");

			var rng = new Random(42);
			var legacy = W(Dist.ArgsNormalization.Legacy);
			var constant = W(Dist.ArgsNormalization.Constant);
			int n = 5000, legacyViol = 0, constViol = 0;
			for (int t = 0; t < n; t++)
			{
				var x = args[rng.Next(args.Count)];
				var y = args[rng.Next(args.Count)];
				if (ReferenceEquals(x, y)) { t--; continue; }
				var A = new List<MethodAnchor.Arg> { x };
				var B = new List<MethodAnchor.Arg> { x, y };
				var C = new List<MethodAnchor.Arg> { y };

				if (Violates(Dist.ArgsDistance(A, B, legacy), Dist.ArgsDistance(B, C, legacy), Dist.ArgsDistance(A, C, legacy))) legacyViol++;
				if (Violates(Dist.ArgsDistance(A, B, constant), Dist.ArgsDistance(B, C, constant), Dist.ArgsDistance(A, C, constant))) constViol++;
			}

			Console.WriteLine($"structured triples: legacy violations = {legacyViol}/{n}, constant = {constViol}/{n}");
			Assert.IsTrue(legacyViol > 0, "Ожидалось хотя бы одно нарушение в Legacy (иначе тест бессмысленен)");
			Assert.AreEqual(0, constViol, "В режиме Constant нарушений быть не должно");
		}

		[TestMethod]
		[TestCategory("Metric/Full")]
		public void AnchorDistance_RandomTriplesOfRealAnchors_SatisfiesAxioms()
		{
			var corpus = CanonicalOrder.Sort(LoadCorpus());
			AnchorsIO.BuildNeighborBags(corpus, NeighborBagKind.Rich, window: 4);

			var w = new Dist.Weights(); // Constant по умолчанию
			var rng = new Random(42);
			int n = 100_000, viol = 0, asym = 0;
			for (int t = 0; t < n; t++)
			{
				var a = corpus[rng.Next(corpus.Count)];
				var b = corpus[rng.Next(corpus.Count)];
				var c = corpus[rng.Next(corpus.Count)];
				double dab = Dist.AnchorDistance(a, b, w), dbc = Dist.AnchorDistance(b, c, w), dac = Dist.AnchorDistance(a, c, w);
				if (Violates(dab, dbc, dac)) viol++;
				if (Math.Abs(dab - Dist.AnchorDistance(b, a, w)) > 1e-9) asym++;
				Assert.IsTrue(dab >= 0 && dab <= 1.0 + Eps, "Расстояние должно лежать в [0,1]");
			}
			Console.WriteLine($"random triples: violations = {viol}/{n}, asymmetries = {asym}/{n}");
			Assert.AreEqual(0, viol, "Неравенство треугольника нарушено");
			Assert.AreEqual(0, asym, "Симметрия нарушена");

			// d(a,a) = 0
			foreach (var a in corpus.Take(200))
				Assert.AreEqual(0.0, Dist.AnchorDistance(a, a, w), Eps);
		}

		[TestMethod]
		[TestCategory("Metric/Full")]
		public void AnchorDistance_ArityMutationTriples_ConstantHolds_LegacyMayViolate()
		{
			// Тройки «якорь, тот же якорь с добавленным аргументом, другой якорь с этим аргументом» —
			// именно такой рисунок возникает при мутациях add_arg/remove_arg.
			var corpus = CanonicalOrder.Sort(LoadCorpus());
			var withArgs = corpus.Where(a => a.Args != null && a.Args.Count > 0).ToList();
			var pool = withArgs.SelectMany(a => a.Args).ToList();
			var rng = new Random(7);
			var legacy = W(Dist.ArgsNormalization.Legacy);
			var constant = W(Dist.ArgsNormalization.Constant);
			int n = 20_000, legacyViol = 0, constViol = 0;
			for (int t = 0; t < n; t++)
			{
				var a = withArgs[rng.Next(withArgs.Count)];
				var extra = pool[rng.Next(pool.Count)];
				var b = Clone(a); b.Args.Add(extra);
				var c = Clone(withArgs[rng.Next(withArgs.Count)]); c.Args = new List<MethodAnchor.Arg> { extra };

				if (Violates(Dist.AnchorDistance(a, b, legacy), Dist.AnchorDistance(b, c, legacy), Dist.AnchorDistance(a, c, legacy))) legacyViol++;
				if (Violates(Dist.AnchorDistance(a, b, constant), Dist.AnchorDistance(b, c, constant), Dist.AnchorDistance(a, c, constant))) constViol++;
			}
			Console.WriteLine($"arity triples (full metric): legacy violations = {legacyViol}/{n}, constant = {constViol}/{n}");
			Assert.AreEqual(0, constViol);
		}

		private static MethodAnchor Clone(MethodAnchor a)
		{
			return new MethodAnchor
			{
				Id = a.Id + "'",
				StartOffset = a.StartOffset,
				EndOffset = a.EndOffset,
				MethodNameNorm = a.MethodNameNorm,
				ParentNameNorm = a.ParentNameNorm,
				ReturnTypeNorm = a.ReturnTypeNorm,
				Args = (a.Args ?? new List<MethodAnchor.Arg>()).Select(x => new MethodAnchor.Arg { TypeNorm = x.TypeNorm, NameNorm = x.NameNorm }).ToList(),
				OrdinalInParent = a.OrdinalInParent,
				NeighborBag = a.NeighborBag,
				File = a.File,
			};
		}
	}
}
