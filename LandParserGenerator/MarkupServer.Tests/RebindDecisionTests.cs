using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Правило принятия решения при перепривязке: tau (абсолютная близость) и
/// относительный margin (отрыв второго кандидата). Чистая функция LandService.DecideCore.
/// </summary>
[TestClass]
public class RebindDecisionTests
{
	private const double Tau = 0.30;
	private const double Margin = 0.15;

	[TestMethod]
	public void Accepts_Close_And_Well_Separated()
	{
		var s = LandService.DecideCore(0.05, 0.40, Tau, Margin, out _);
		Assert.AreEqual(LandService.RebindStatus.Accepted, s);
	}

	[TestMethod]
	public void Accepts_Exact_Match_With_Distinct_Second()
	{
		// d1 = 0: относительный отрыв = 1 при любом d2 > 0
		var s = LandService.DecideCore(0.0, 0.01, Tau, Margin, out _);
		Assert.AreEqual(LandService.RebindStatus.Accepted, s);
	}

	[TestMethod]
	public void Accepts_Single_Candidate_Within_Tau()
	{
		var s = LandService.DecideCore(0.20, null, Tau, Margin, out _);
		Assert.AreEqual(LandService.RebindStatus.Accepted, s);
	}

	[TestMethod]
	public void Ambiguous_When_Second_Is_Almost_As_Close()
	{
		// (0.22 - 0.20) / 0.22 = 0.09 < 0.15
		var s = LandService.DecideCore(0.20, 0.22, Tau, Margin, out var reason);
		Assert.AreEqual(LandService.RebindStatus.Ambiguous, s, reason);
	}

	[TestMethod]
	public void Ambiguous_On_Exact_Tie_Including_Two_Exact_Matches()
	{
		Assert.AreEqual(LandService.RebindStatus.Ambiguous, LandService.DecideCore(0.10, 0.10, Tau, Margin, out _));
		Assert.AreEqual(LandService.RebindStatus.Ambiguous, LandService.DecideCore(0.0, 0.0, Tau, Margin, out _));
	}

	[TestMethod]
	public void Lost_When_Best_Is_Beyond_Tau()
	{
		var s = LandService.DecideCore(0.31, 0.90, Tau, Margin, out _);
		Assert.AreEqual(LandService.RebindStatus.Lost, s);
	}

	[TestMethod]
	public void Lost_When_No_Candidates()
	{
		var s = LandService.DecideCore(double.NaN, null, Tau, Margin, out _);
		Assert.AreEqual(LandService.RebindStatus.Lost, s);
	}

	[TestMethod]
	public void Margin_Is_Scale_Invariant()
	{
		// Одинаковое относительное разделение → одинаковый вердикт при разной абсолютной близости
		var near = LandService.DecideCore(0.02, 0.04, 1.0, Margin, out _);
		var far = LandService.DecideCore(0.20, 0.40, 1.0, Margin, out _);
		Assert.AreEqual(near, far);
		Assert.AreEqual(LandService.RebindStatus.Accepted, near);
	}
}
