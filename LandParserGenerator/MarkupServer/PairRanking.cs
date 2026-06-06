using System;
using System.Collections.Generic;
using System.Linq;

namespace MarkupServer
{
    /// <summary>
    /// Степенная нормализация (power normalization, k=2) и ранжирование
    /// пар (a, b) ∈ C_A × C_B по совместному скору:
    ///
    ///   rank(a, b) = P(a) · P(b) · p(a, b)         если p ≥ τ_pair
    ///   rank(a, b) = 0                              иначе
    ///
    ///   где P(a) = S1_A(a)² / Σ S1_A(a')²
    ///       P(b) = S1_B(b)² / Σ S1_B(b')²
    ///
    /// Параметры калибровались на 33 фикстурах python-прототипа,
    /// см. markup/experiments/coupled_rebind/RANKING_EXPERIMENT.md
    /// Top-1 точность: 87.9% (29/33).
    ///
    /// Эквивалент: softmax(β=10) на тех же данных даёт идентичные ответы.
    /// Power normalization выбрана как более простая для объяснения формула
    /// (нет экспоненты, степень k=2 интуитивна).
    /// </summary>
    public static class PairRanking
    {
        /// <summary>Дефолтная степень нормализации.</summary>
        public const double DEFAULT_K = 2.0;

        /// <summary>Дефолтный порог осмысленности пары (см. CoupledRebind).</summary>
        public const double DEFAULT_TAU_PAIR = 0.60;

        /// <summary>
        /// Степенная нормализация: vals[i]^k / Σ vals[j]^k.
        /// При пустом или нулевом vals возвращает равномерное распределение.
        /// </summary>
        public static double[] PowerNormalize(IReadOnlyList<double> vals, double k)
        {
            if (vals == null || vals.Count == 0) return Array.Empty<double>();

            var n = vals.Count;
            var pows = new double[n];
            double total = 0.0;
            for (int i = 0; i < n; i++)
            {
                var v = Math.Max(vals[i], 0.0);
                pows[i] = Math.Pow(v, k);
                total += pows[i];
            }

            var result = new double[n];
            if (total <= 0.0)
            {
                // все vals = 0 — отдаём равномерное, чтобы не сломать произведение
                var uniform = 1.0 / n;
                for (int i = 0; i < n; i++) result[i] = uniform;
                return result;
            }

            for (int i = 0; i < n; i++)
                result[i] = pows[i] / total;
            return result;
        }

        /// <summary>Одна пара с финальным скором.</summary>
        public sealed class ScoredPair
        {
            /// <summary>Индекс a в исходной колонке C_A.</summary>
            public int AIdx { get; init; }
            /// <summary>Индекс b в исходной колонке C_B.</summary>
            public int BIdx { get; init; }
            /// <summary>S1_A(a), [0, 1] — близость к старому якорю A.</summary>
            public double S1A { get; init; }
            /// <summary>S1_B(b), [0, 1].</summary>
            public double S1B { get; init; }
            /// <summary>P_A(a) = S1_A(a)^k / Σ S1_A^k — нормализованная доля кандидата в колонке A.</summary>
            public double PA { get; init; }
            /// <summary>P_B(b) = S1_B(b)^k / Σ S1_B^k.</summary>
            public double PB { get; init; }
            /// <summary>SoftPairScore(a, b), [0, 1].</summary>
            public double P { get; init; }
            /// <summary>Финальный ранг = P_A(a) · P_B(b) · p(a, b).</summary>
            public double Rank { get; init; }
        }

        /// <summary>
        /// Ранжирует все K_A × K_B пар по power_norm(k=2) × p,
        /// отсекая пары с p &lt; tauPair, и возвращает топ-N.
        /// </summary>
        /// <param name="s1A">S1 для каждого кандидата в C_A.</param>
        /// <param name="s1B">S1 для каждого кандидата в C_B.</param>
        /// <param name="pMatrix">Матрица SoftPairScore размером |C_A| × |C_B|.</param>
        /// <param name="topN">Сколько лучших пар вернуть.</param>
        /// <param name="k">Степень нормализации (по умолчанию 2).</param>
        /// <param name="tauPair">Порог осмысленности пары (по умолчанию 0.60).</param>
        public static List<ScoredPair> RankTopN(
            IReadOnlyList<double> s1A,
            IReadOnlyList<double> s1B,
            IReadOnlyList<IReadOnlyList<double>> pMatrix,
            int topN,
            double k = DEFAULT_K,
            double tauPair = DEFAULT_TAU_PAIR)
        {
            if (s1A == null || s1B == null || pMatrix == null) return new List<ScoredPair>();
            var nA = s1A.Count;
            var nB = s1B.Count;
            if (nA == 0 || nB == 0) return new List<ScoredPair>();
            if (pMatrix.Count != nA) return new List<ScoredPair>();

            var pa = PowerNormalize(s1A, k);
            var pb = PowerNormalize(s1B, k);

            var pool = new List<ScoredPair>(nA * nB);
            for (int i = 0; i < nA; i++)
            {
                var row = pMatrix[i];
                if (row == null || row.Count != nB) continue;
                for (int j = 0; j < nB; j++)
                {
                    var p = row[j];
                    if (p < tauPair) continue;
                    var rank = pa[i] * pb[j] * p;
                    pool.Add(new ScoredPair
                    {
                        AIdx = i, BIdx = j,
                        S1A = s1A[i], S1B = s1B[j],
                        PA = pa[i], PB = pb[j],
                        P = p, Rank = rank,
                    });
                }
            }

            pool.Sort((x, y) => y.Rank.CompareTo(x.Rank));
            if (topN > 0 && pool.Count > topN)
                pool = pool.GetRange(0, topN);
            return pool;
        }
    }
}
