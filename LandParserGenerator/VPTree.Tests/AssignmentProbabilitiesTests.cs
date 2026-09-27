using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VPTree;
using static VPTree.AssignmentProbabilities;

namespace Tests
{
	/// <summary>
	/// Проценты перепривязки (VPTreeLib.AssignmentProbabilities). Ожидаемые числа — из эталонной реализации на Python
	/// (markup/experiments/_explore/2026-09-27_distance_questions/scripts/common.py, results/calibration_mockup_*.csv):
	/// пример макета — тип User, точки name, login, nick.
	/// </summary>
	[TestClass]
	public class AssignmentProbabilitiesTests
	{
		private const double T = DefaultTemperature;
		private static readonly double R = 0.30 / (1 - 0.30);
		private const int Name = 0, FullName = 1, Handle = 2;

		private static IReadOnlyList<IReadOnlyList<(int, double)>> Mockup() => new List<IReadOnlyList<(int, double)>>
		{
			new List<(int, double)> { (Name, 0.10), (FullName, 0.14) },                  // name
			new List<(int, double)> { (Name, 0.11), (FullName, 0.18) },                  // login
			new List<(int, double)> { (Handle, 0.24), (Name, 0.25), (FullName, 0.28) },  // nick
		};

		[TestMethod]
		public void Point_Softmax_With_None_At_Reject_Cost()
		{
			var p = Point(new[] { 0.10, 0.14 }, T, R);
			Assert.AreEqual(3, p.Length);
			Assert.AreEqual(0.660, p[0], 5e-4);
			Assert.AreEqual(0.337, p[1], 5e-4);
			Assert.AreEqual(0.003, p[2], 5e-4);
			Assert.AreEqual(1.0, p.Sum(), 1e-12);
		}

		[TestMethod]
		public void Point_Without_Candidates_Is_Surely_None()
		{
			var p = Point(Array.Empty<double>(), T, R);
			CollectionAssert.AreEqual(new[] { 1.0 }, p);
		}

		[TestMethod]
		public void Mockup_Variants_And_Rows_Match_Python_Reference()
		{
			var dist = Component(Mockup(), T, R);
			Assert.IsTrue(dist.Exact);
			Assert.AreEqual(20, dist.VariantCount);

			Assert.AreEqual(3, dist.Top.Count);
			Assert.AreEqual(0.49, dist.Top[0].Cost, 1e-12);
			Assert.AreEqual(0.5841, dist.Top[0].P, 5e-5);
			CollectionAssert.AreEqual(new[] { FullName, Name, Handle }, dist.Top[0].Choice);
			Assert.AreEqual(0.3529, dist.Top[1].P, 5e-5);
			CollectionAssert.AreEqual(new[] { Name, FullName, Handle }, dist.Top[1].Choice);
			Assert.AreEqual(0.0246, dist.Top[2].P, 5e-5);
			CollectionAssert.AreEqual(new[] { FullName, Name, None }, dist.Top[2].Choice);

			Assert.AreEqual(0.6139, dist.RowP(0, FullName), 5e-5);
			Assert.AreEqual(0.3762, dist.RowP(0, Name), 5e-5);
			Assert.AreEqual(0.0099, dist.RowP(0, None), 5e-5);
			Assert.AreEqual(0.6158, dist.RowP(1, Name), 5e-5);
			Assert.AreEqual(0.3705, dist.RowP(1, FullName), 5e-5);
			Assert.AreEqual(0.0137, dist.RowP(1, None), 5e-5);
			Assert.AreEqual(0.9512, dist.RowP(2, Handle), 5e-5);
			Assert.AreEqual(0.0401, dist.RowP(2, None), 5e-5);
			Assert.AreEqual(0.0051, dist.RowP(2, FullName), 5e-5);
			Assert.AreEqual(0.0036, dist.RowP(2, Name), 5e-5);

			for (int i = 0; i < 3; i++)
				Assert.AreEqual(1.0, dist.Rows[i].Values.Sum(), 1e-12, $"row {i} must sum to 1");
		}

		[TestMethod]
		public void Row_Is_Sum_Of_Variants_And_Bounds_The_Variant()
		{
			var dist = Component(Mockup(), T, R, topCount: 1000);
			Assert.AreEqual(20, dist.Top.Count);
			for (int i = 0; i < 3; i++)
				foreach (var kv in dist.Rows[i])
					Assert.AreEqual(kv.Value, dist.Top.Where(v => v.Choice[i] == kv.Key).Sum(v => v.P), 1e-12);
			var best = dist.Top[0];
			for (int i = 0; i < 3; i++)
				Assert.IsTrue(best.P <= dist.RowP(i, best.Choice[i]) + 1e-12, "variant % must not exceed any of its rows");
		}

		[TestMethod]
		public void User_Choice_Conditions_The_Distribution()
		{
			// выбор name → name: «уверенность с вашими правками» 94 %, login уходит в fullName с 98 %
			var dist = Component(Mockup(), T, R, new Dictionary<int, int> { [0] = Name });
			Assert.AreEqual(0.938, dist.Top[0].P, 5e-4);
			CollectionAssert.AreEqual(new[] { Name, FullName, Handle }, dist.Top[0].Choice);
			Assert.AreEqual(1.0, dist.RowP(0, Name), 1e-12);
			Assert.AreEqual(0.98, dist.RowP(1, FullName), 5e-3);
			Assert.AreEqual(0.0, dist.RowP(1, Name), 1e-12, "field taken by the user's choice");
		}

		[TestMethod]
		public void Choosing_None_Is_A_Valid_Condition()
		{
			var dist = Component(Mockup(), T, R, new Dictionary<int, int> { [2] = None });
			Assert.AreEqual(1.0, dist.RowP(2, None), 1e-12);
			Assert.AreEqual(1.0, dist.Rows[0].Values.Sum(), 1e-12);
		}

		[TestMethod]
		public void Conflicting_Choices_Give_Empty_Distribution()
		{
			var dist = Component(Mockup(), T, R, new Dictionary<int, int> { [0] = Name, [1] = Name });
			Assert.AreEqual(0, dist.Top.Count);
			Assert.AreEqual(0, dist.VariantCount);
			Assert.AreEqual(0.0, dist.RowP(0, Name));
		}

		[TestMethod]
		public void Choice_Of_Unknown_Candidate_Gives_Empty_Distribution()
		{
			var dist = Component(Mockup(), T, R, new Dictionary<int, int> { [0] = Handle });
			Assert.AreEqual(0, dist.Top.Count);
		}

		[TestMethod]
		public void Single_Point_Component_Equals_Point_Formula()
		{
			var edges = new List<IReadOnlyList<(int, double)>> { new List<(int, double)> { (7, 0.10), (9, 0.14) } };
			var dist = Component(edges, T, R);
			var p = Point(new[] { 0.10, 0.14 }, T, R);
			Assert.AreEqual(p[0], dist.RowP(0, 7), 1e-12);
			Assert.AreEqual(p[1], dist.RowP(0, 9), 1e-12);
			Assert.AreEqual(p[2], dist.RowP(0, None), 1e-12);
		}

		[TestMethod]
		public void Murty_Path_Matches_Enumeration_When_It_Covers_All_Variants()
		{
			var exact = Component(Mockup(), T, R, topCount: 5);
			var viaMurty = Component(Mockup(), T, R, topCount: 5, enumerateLimit: 0, approxVariants: 1000);
			Assert.IsTrue(viaMurty.Exact, "fewer variants than requested — space exhausted");
			Assert.AreEqual(exact.VariantCount, viaMurty.VariantCount);
			for (int i = 0; i < 3; i++)
				foreach (var kv in exact.Rows[i])
					Assert.AreEqual(kv.Value, viaMurty.RowP(i, kv.Key), 1e-9);
			for (int k = 0; k < 5; k++)
			{
				Assert.AreEqual(exact.Top[k].Cost, viaMurty.Top[k].Cost, 1e-9);
				Assert.AreEqual(exact.Top[k].P, viaMurty.Top[k].P, 1e-9);
			}
		}

		[TestMethod]
		public void Murty_Path_With_User_Choice_Matches_Enumeration()
		{
			var fixedChoice = new Dictionary<int, int> { [0] = Name };
			var exact = Component(Mockup(), T, R, fixedChoice);
			var viaMurty = Component(Mockup(), T, R, fixedChoice, enumerateLimit: 0, approxVariants: 1000);
			for (int i = 0; i < 3; i++)
				foreach (var kv in exact.Rows[i])
					Assert.AreEqual(kv.Value, viaMurty.RowP(i, kv.Key), 1e-9);
		}

		[TestMethod]
		public void Truncated_Murty_Is_Marked_Approximate()
		{
			var dist = Component(Mockup(), T, R, enumerateLimit: 0, approxVariants: 4);
			Assert.IsFalse(dist.Exact);
			Assert.AreEqual(4, dist.VariantCount);
		}

		[TestMethod]
		public void Result_Does_Not_Depend_On_Candidate_Order()
		{
			var shuffled = new List<IReadOnlyList<(int, double)>>
			{
				new List<(int, double)> { (FullName, 0.14), (Name, 0.10), (FullName, 0.50) },   // дубль с большим d игнорируется
				new List<(int, double)> { (FullName, 0.18), (Name, 0.11) },
				new List<(int, double)> { (FullName, 0.28), (Handle, 0.24), (Name, 0.25) },
			};
			var a = Component(Mockup(), T, R);
			var b = Component(shuffled, T, R);
			for (int i = 0; i < 3; i++)
				foreach (var kv in a.Rows[i])
					Assert.AreEqual(kv.Value, b.RowP(i, kv.Key), 1e-12);
			CollectionAssert.AreEqual(a.Top[0].Choice, b.Top[0].Choice);
		}

		[TestMethod]
		public void Temperature_Must_Be_Positive()
		{
			Assert.ThrowsException<ArgumentException>(() => Point(new[] { 0.1 }, 0.0, R));
			Assert.ThrowsException<ArgumentException>(() => Component(Mockup(), -1.0, R));
		}
	}
}
