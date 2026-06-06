using System.Collections.Generic;
using System.Linq;
using MarkupServer;

namespace MarkupServer.Tests;

/// <summary>
/// Тесты степенной нормализации и ранжирования пар.
/// Воспроизводят числовые сценарии из
/// markup/experiments/coupled_rebind/RANKING_EXPERIMENT.md.
/// </summary>
[TestClass]
public class PairRankingTests
{
    private const double EPS = 1e-3;

    // ---------- PowerNormalize ----------

    [TestMethod]
    public void PowerNormalize_uniform_input_gives_equal_shares()
    {
        var r = PairRanking.PowerNormalize(new[] { 0.5, 0.5, 0.5 }, 2);
        Assert.AreEqual(3, r.Length);
        foreach (var v in r) Assert.AreEqual(1.0 / 3.0, v, EPS);
        Assert.AreEqual(1.0, r.Sum(), EPS);
    }

    [TestMethod]
    public void PowerNormalize_all_zeros_gives_uniform_fallback()
    {
        var r = PairRanking.PowerNormalize(new[] { 0.0, 0.0 }, 2);
        Assert.AreEqual(0.5, r[0], EPS);
        Assert.AreEqual(0.5, r[1], EPS);
    }

    [TestMethod]
    public void PowerNormalize_squared_user_example()
    {
        // S1: 0.8 и 0.7 → P должно быть 0.566/0.434 (из RANKING_EXPERIMENT.md)
        var r = PairRanking.PowerNormalize(new[] { 0.8, 0.7 }, 2);
        Assert.AreEqual(0.566, r[0], EPS);
        Assert.AreEqual(0.434, r[1], EPS);
        Assert.AreEqual(1.0, r[0] + r[1], EPS);
    }

    [TestMethod]
    public void PowerNormalize_big_margin_concentrates_on_leader()
    {
        // S1: 0.9 vs 0.2 → лидер ~95%
        var r = PairRanking.PowerNormalize(new[] { 0.9, 0.2 }, 2);
        Assert.AreEqual(0.953, r[0], EPS);
        Assert.AreEqual(0.047, r[1], EPS);
    }

    [TestMethod]
    public void PowerNormalize_k1_equals_linear_normalization()
    {
        // k=1 это просто линейная нормализация
        var r = PairRanking.PowerNormalize(new[] { 0.8, 0.7 }, 1);
        Assert.AreEqual(0.8 / 1.5, r[0], EPS);
        Assert.AreEqual(0.7 / 1.5, r[1], EPS);
    }

    [TestMethod]
    public void PowerNormalize_high_k_approaches_winner_take_all()
    {
        // k=10 на (0.9, 0.7): 0.9^10 / (0.9^10 + 0.7^10) ≈ 0.925
        // Для сравнения при k=2 на тех же входах лидер забирает только ≈0.623.
        // Тест проверяет, что повышение k действительно сильно концентрирует массу на лидере.
        var rK10 = PairRanking.PowerNormalize(new[] { 0.9, 0.7 }, 10);
        Assert.IsTrue(rK10[0] > 0.90, $"при k=10 top-1 должен забрать >0.90, получили {rK10[0]:F3}");

        var rK2 = PairRanking.PowerNormalize(new[] { 0.9, 0.7 }, 2);
        Assert.IsTrue(rK10[0] > rK2[0] + 0.20,
            $"k=10 должен концентрировать заметно сильнее чем k=2 (k10={rK10[0]:F3}, k2={rK2[0]:F3})");
    }

    [TestMethod]
    public void PowerNormalize_very_high_k_with_big_margin_approaches_one()
    {
        // (0.9, 0.5) при k=10: 0.9^10 / (0.9^10 + 0.5^10) ≈ 0.997 — практически winner-take-all
        var r = PairRanking.PowerNormalize(new[] { 0.9, 0.5 }, 10);
        Assert.IsTrue(r[0] > 0.99, $"top-1 на большом отрыве и k=10 должен забрать ≥0.99, получили {r[0]:F3}");
    }

    // ---------- RankTopN ----------

    [TestMethod]
    public void RankTopN_contested_pair_picks_higher_S1_leader()
    {
        // Тот самый «contested» сценарий из обсуждения:
        // f1 S1=0.8, d1 S1=0.7 / f2 S1=0.9, d2 S1=0.9
        // p(f1,f2)=0.9, p(d1,d2)=0.95, кросс ниже τ
        var s1A = new[] { 0.8, 0.7 };
        var s1B = new[] { 0.9, 0.9 };
        var pm = new List<IReadOnlyList<double>>
        {
            new List<double> { 0.90, 0.20 },  // f1 row
            new List<double> { 0.20, 0.95 },  // d1 row
        };

        var top = PairRanking.RankTopN(s1A, s1B, pm, topN: 5, k: 2.0, tauPair: 0.60);

        Assert.IsTrue(top.Count >= 2, "Должны выжить как минимум обе диагональные пары");
        // Победитель — (f1, f2) благодаря большему S1 у f1
        Assert.AreEqual(0, top[0].AIdx);
        Assert.AreEqual(0, top[0].BIdx);
        Assert.IsTrue(top[0].Rank > top[1].Rank, "Отрыв (f1,f2) над (d1,d2) должен быть положительным");

        // Численная проверка: power-norm даёт 0.566/0.434, P_B одинаковы 0.5
        var rankF = 0.566 * 0.5 * 0.90;
        var rankD = 0.434 * 0.5 * 0.95;
        Assert.AreEqual(rankF, top[0].Rank, EPS);
        Assert.AreEqual(rankD, top[1].Rank, EPS);
    }

    [TestMethod]
    public void RankTopN_filters_pairs_below_tau()
    {
        // Все p ниже τ_pair → топ пустой
        var s1A = new[] { 0.8, 0.7 };
        var s1B = new[] { 0.9, 0.9 };
        var pm = new List<IReadOnlyList<double>>
        {
            new List<double> { 0.30, 0.25 },
            new List<double> { 0.20, 0.40 },
        };
        var top = PairRanking.RankTopN(s1A, s1B, pm, topN: 5, k: 2.0, tauPair: 0.60);
        Assert.AreEqual(0, top.Count, "Все пары ниже τ должны быть отсечены");
    }

    [TestMethod]
    public void RankTopN_respects_topN_limit()
    {
        var s1A = new[] { 0.9, 0.8, 0.7 };
        var s1B = new[] { 0.9, 0.8, 0.7 };
        // Все пары осмысленны (p ≥ 0.7), всего 9
        var pm = new List<IReadOnlyList<double>>
        {
            new List<double> { 0.95, 0.85, 0.75 },
            new List<double> { 0.85, 0.90, 0.80 },
            new List<double> { 0.75, 0.80, 0.85 },
        };
        var top = PairRanking.RankTopN(s1A, s1B, pm, topN: 3, k: 2.0, tauPair: 0.60);
        Assert.AreEqual(3, top.Count, "Должен вернуть ровно top-3");
        // Должны быть отсортированы по убыванию ранга
        Assert.IsTrue(top[0].Rank >= top[1].Rank);
        Assert.IsTrue(top[1].Rank >= top[2].Rank);
    }

    [TestMethod]
    public void RankTopN_empty_inputs_return_empty()
    {
        var top = PairRanking.RankTopN(
            new double[] { }, new double[] { }, new List<IReadOnlyList<double>>(),
            topN: 5, k: 2.0, tauPair: 0.60);
        Assert.AreEqual(0, top.Count);
    }

    [TestMethod]
    public void RankTopN_dimension_mismatch_returns_empty()
    {
        // S1_A длиной 2, а матрица только 1 строка → невалидно
        var s1A = new[] { 0.8, 0.7 };
        var s1B = new[] { 0.9 };
        var pm = new List<IReadOnlyList<double>>
        {
            new List<double> { 0.9 },
        };
        var top = PairRanking.RankTopN(s1A, s1B, pm, topN: 5, k: 2.0, tauPair: 0.60);
        Assert.AreEqual(0, top.Count);
    }

    [TestMethod]
    public void RankTopN_equal_S1_falls_back_to_pure_p()
    {
        // S1 одинаковы → power-norm = uniform → ранжируем чисто по p
        var s1A = new[] { 0.8, 0.8 };
        var s1B = new[] { 0.8, 0.8 };
        var pm = new List<IReadOnlyList<double>>
        {
            new List<double> { 0.70, 0.65 },
            new List<double> { 0.95, 0.75 },
        };
        var top = PairRanking.RankTopN(s1A, s1B, pm, topN: 5, k: 2.0, tauPair: 0.60);
        // (d1, f2) с p=0.95 должна быть top-1
        Assert.AreEqual(1, top[0].AIdx);
        Assert.AreEqual(0, top[0].BIdx);
    }

    [TestMethod]
    public void RankTopN_strong_leader_dominates_even_with_lower_p()
    {
        // Лидер по S1 имеет огромный отрыв, но чуть слабее по p — он всё равно побеждает
        var s1A = new[] { 0.9, 0.2 };
        var s1B = new[] { 0.9, 0.9 };
        var pm = new List<IReadOnlyList<double>>
        {
            new List<double> { 0.80, 0.20 },  // f1: p=0.8 с f2
            new List<double> { 0.20, 0.99 },  // d1: p=0.99 с d2
        };
        var top = PairRanking.RankTopN(s1A, s1B, pm, topN: 5, k: 2.0, tauPair: 0.60);
        // (f1, f2): P(f1)≈0.953, P(f2)=0.5, p=0.8 → 0.381
        // (d1, d2): P(d1)≈0.047, P(d2)=0.5, p=0.99 → 0.023
        // → f1 побеждает несмотря на лучший p у d1
        Assert.AreEqual(0, top[0].AIdx);
        Assert.AreEqual(0, top[0].BIdx);
        Assert.IsTrue(top[0].Rank > 0.30, $"rank(f1, f2) должен быть ~0.38, получили {top[0].Rank:F3}");
    }
}
