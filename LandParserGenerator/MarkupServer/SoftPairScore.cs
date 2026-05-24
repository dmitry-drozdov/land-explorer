using System;
using System.Collections.Generic;
using System.Linq;

namespace MarkupServer
{
    /// <summary>
    /// SoftPairScore — функция совместимости пары (a, b) в [0, 1].
    ///
    /// Это «голос 2» из алгоритма coupled rebind. Отвечает на вопрос:
    /// «насколько кандидаты a (GraphQL) и b (TypeScript) образуют осмысленную
    /// пару в текущем коде?»
    ///
    /// Ключевое отличие от ScoreTsToGql из LandService.Helpers.cs:954: там
    /// стояло MethodNameNorm != MethodNameNorm => -∞ (жёсткий equals на имени),
    /// что ломает пару при rename. Здесь имя — мягкое сравнение через Левенштейн.
    ///
    /// Веса калибровались на 33 фикстурах из markup/experiments/coupled_rebind/
    /// (см. REPORT.md в той же папке). Сумма весов = 1.0, итог в [0, 1].
    /// </summary>
    public static class SoftPairScore
    {
        public sealed class Weights
        {
            public double Name = 0.35;
            public double Parent = 0.25;
            public double Returns = 0.20;
            public double Args = 0.10;
            public double FileBonus = 0.10;

            public double Total() => Name + Parent + Returns + Args + FileBonus;
        }

        public static readonly Weights DefaultWeights = new();

        public sealed class Breakdown
        {
            public double NameSim;
            public double ParentSim;
            public double ReturnsSim;
            public double ArgsSim;
            public double FileBonus;
            public double Total;
        }

        /// <summary>
        /// Полный расчёт SoftPairScore с покомпонентной разбивкой. Для UI
        /// и диагностики.
        /// </summary>
        public static Breakdown Compute(TreeNode a, TreeNode b, Weights w = null)
        {
            w ??= DefaultWeights;
            if (a == null || b == null)
                return new Breakdown();

            // Hard cut-off: пара одного языка — не GraphQL↔TS пара.
            if (string.Equals(a.Language ?? "", b.Language ?? "", StringComparison.OrdinalIgnoreCase))
                return new Breakdown();

            // Выясняем, какая сторона gql, какая ts (порядок аргументов не важен).
            var (gql, ts) = string.Equals(a.Language, "gql", StringComparison.OrdinalIgnoreCase)
                ? (a, b)
                : (b, a);

            var nameSim = LevSimilarity(
                Norm(gql.MethodNameNorm ?? gql.Name),
                Norm(ts.MethodNameNorm ?? ts.Name));

            var parentSim = ParentSimilarity(
                gql.ParentNameNorm ?? gql.ParentNameRaw,
                ts.ParentNameNorm ?? ts.ParentNameRaw);

            var returnsSim = LevSimilarity(
                Norm(gql.ReturnTypeNorm ?? ""),
                Norm(ts.ReturnTypeNorm ?? ""));

            var argsSim = ArgsSimilarity(gql.Args, ts.Args);
            var fileBonus = FileBonusForTs(ts);

            var total =
                  w.Name * nameSim
                + w.Parent * parentSim
                + w.Returns * returnsSim
                + w.Args * argsSim
                + w.FileBonus * fileBonus;

            return new Breakdown
            {
                NameSim = nameSim,
                ParentSim = parentSim,
                ReturnsSim = returnsSim,
                ArgsSim = argsSim,
                FileBonus = fileBonus,
                Total = total,
            };
        }

        public static double Score(TreeNode a, TreeNode b, Weights w = null) =>
            Compute(a, b, w).Total;

        // ---------- Внутренние помощники ----------

        /// <summary>
        /// Нормализация имени: camelCase / snake_case → "words separated by spaces"
        /// в lowercase. GraphQL-модификаторы (!, [, ]) удаляются.
        /// </summary>
        public static string Norm(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var s = raw.Replace("!", "").Replace("[", "").Replace("]", "").Trim();
            s = s.Replace('_', ' ');

            var sb = new System.Text.StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (char.IsUpper(ch) && i > 0)
                {
                    var prev = s[i - 1];
                    var next = i + 1 < s.Length ? s[i + 1] : '\0';
                    if (char.IsLower(prev) || char.IsDigit(prev))
                        sb.Append(' ');
                    else if (char.IsUpper(prev) && char.IsLower(next))
                        sb.Append(' ');
                }
                sb.Append(ch);
            }

            return string.Join(' ',
                sb.ToString().ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Классический Левенштейн O(|a|·|b|), O(min) памяти.</summary>
        public static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            if (a.Length < b.Length) (a, b) = (b, a);

            var prev = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            var curr = new int[b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
        }

        /// <summary>Сходство в [0, 1]: 1 если строки совпадают, 0 если совсем разные.</summary>
        public static double LevSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            var maxLen = Math.Max(a.Length, b.Length);
            return 1.0 - (double)Levenshtein(a, b) / maxLen;
        }

        internal static double ParentSimilarity(string parentA, string parentB)
        {
            var a = Norm(parentA ?? "");
            var b = Norm(parentB ?? "");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            if (a == b) return 1.0;
            if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
                return 0.5;

            var sa = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var sb = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (sa.Count == 0 || sb.Count == 0) return 0.0;
            var inter = sa.Intersect(sb).Count();
            var union = sa.Union(sb).Count();
            return union == 0 ? 0.0 : (double)inter / union;
        }

        internal static double ArgsSimilarity(List<Arg> argsA, List<Arg> argsB)
        {
            var a = argsA ?? new List<Arg>();
            var b = argsB ?? new List<Arg>();

            if (a.Count == 0 && b.Count == 0) return 1.0;
            if (a.Count == 0 || b.Count == 0) return 0.0;

            var matched = new HashSet<int>();
            double score = 0.0;
            foreach (var ai in a)
            {
                double best = 0.0;
                int bestIdx = -1;
                var na = Norm(ai.NameNorm ?? "");
                var ta = Norm(ai.TypeNorm ?? "");
                for (int j = 0; j < b.Count; j++)
                {
                    if (matched.Contains(j)) continue;
                    var nb = Norm(b[j].NameNorm ?? "");
                    var tb = Norm(b[j].TypeNorm ?? "");
                    double cand;
                    if (na == nb && ta == tb) cand = 1.0;
                    else if (ta == tb) cand = 0.5;
                    else cand = 0.0;
                    if (cand > best)
                    {
                        best = cand;
                        bestIdx = j;
                    }
                }
                if (bestIdx >= 0)
                {
                    score += best;
                    matched.Add(bestIdx);
                }
            }
            var denom = Math.Max(a.Count, b.Count);
            return denom == 0 ? 0.0 : score / denom;
        }

        internal static double FileBonusForTs(TreeNode ts)
        {
            var fp = (ts?.Filepath ?? "").Replace('\\', '/').ToLowerInvariant();
            return fp.Contains("resolver") ? 1.0 : 0.0;
        }
    }
}
