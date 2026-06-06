using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MarkupServer
{
    /// <summary>
    /// Coupled Anchor Rebinding — перепривязка пары точек (GraphQL ↔ TS).
    ///
    /// Алгоритм calibrated в Python-прототипе:
    ///   markup/experiments/coupled_rebind/REPORT.md
    /// Воспроизводимость:
    ///   markup/experiments/coupled_rebind/REPRODUCIBILITY.md
    ///
    /// Решение принимается по трём независимым условиям (НЕ агрегированный скор):
    ///   1) margin_A = S1(top-1 в C_A) − S1(top-2 в C_A) ≥ M_LOCAL
    ///   2) margin_B = S1(top-1 в C_B) − S1(top-2 в C_B) ≥ M_LOCAL
    ///   3) SoftPairScore(top-1, top-1) ≥ TAU_PAIR
    /// Если все три — auto-accept; иначе UI-picker с двумя колонками.
    /// </summary>
    public partial class LandService
    {
        // Калиброванные пороги. См. REPORT.md, секция "Калибровка порогов".
        private const int K_CANDIDATES = 5;
        private const double M_LOCAL = 0.08;
        private const double TAU_PAIR = 0.60;
        private const double LAMBDA_S1 = 1.0;

        // Параметр δ для предупреждения «есть пара с большим SoftPairScore».
        private const double WARNING_DELTA = 0.05;

        private sealed class RankedCandidate
        {
            public TreeNode Node;
            public double S1;
            public double Distance;
        }

        // ---------- KNearest c K кандидатами (наследует логику из Helpers) ----------

        /// <summary>
        /// Top-K кандидатов для oldNode из соответствующего профильного VP-дерева.
        /// Возвращает список упорядоченный по убыванию S1.
        /// </summary>
        private List<RankedCandidate> RebindAnchorTopK(TreeNode oldNode, int k)
        {
            if (oldNode == null) return new List<RankedCandidate>();

            EnsureAnchorFamily(oldNode);
            var profileKey = GetProfileKey(oldNode);
            if (string.IsNullOrWhiteSpace(profileKey))
                return new List<RankedCandidate>();

            if (!currentTreesByProfileKey.TryGetValue(profileKey, out var tree)
                || !currentContextsByProfileKey.TryGetValue(profileKey, out var contexts)
                || !currentNodesByProfileKey.TryGetValue(profileKey, out var nodes)
                || contexts.Count == 0)
                return new List<RankedCandidate>();

            var query = BuildAnchorContext(oldNode);
            if (query == null) return new List<RankedCandidate>();

            var raw = tree.KNearest(query, Math.Min(k, contexts.Count));
            if (raw == null || raw.Count == 0)
                return new List<RankedCandidate>();

            return raw
                .Select(r => new RankedCandidate
                {
                    Node = CloneAnchor(nodes[r.Index]),
                    Distance = r.Dist,
                    S1 = Math.Exp(-LAMBDA_S1 * r.Dist),
                })
                .OrderByDescending(rc => rc.S1)
                .ToList();
        }

        // ---------- DTO для клиента ----------

        public sealed class CoupledRebindParams
        {
            /// <summary>id одной из двух точек пары (либо A, либо B — сервер найдёт парную).</summary>
            public string anchorId { get; set; }
            /// <summary>Опциональный K (если не задан — используется константа).</summary>
            public int? k { get; set; }
        }

        /// <summary>Один кандидат для UI-колонки.</summary>
        public sealed class CoupledRebindCandidateDto
        {
            public TreeNodeClientV2 n { get; set; }
            /// <summary>S1 (близость к старому якорю), [0, 1].</summary>
            public double s1 { get; set; }
        }

        /// <summary>Ответ клиенту: один из двух режимов.</summary>
        public sealed class CoupledRebindResultDto
        {
            /// <summary>"accepted" — авто-перепривязка выполнена; "picker" — UI должен спросить.</summary>
            public string kind { get; set; }

            /// <summary>ID старого якоря A (gql-сторона пары). Нужен клиенту для последующего commit.</summary>
            public string anchorIdA { get; set; }
            /// <summary>ID старого якоря B (ts-сторона пары).</summary>
            public string anchorIdB { get; set; }

            // accepted-only:
            public TreeNodeClientV2 a { get; set; }
            public TreeNodeClientV2 b { get; set; }
            public double? pairScore { get; set; }
            public double? marginA { get; set; }
            public double? marginB { get; set; }

            // picker-only:
            public List<CoupledRebindCandidateDto> colA { get; set; }
            public List<CoupledRebindCandidateDto> colB { get; set; }
            /// <summary>Матрица K_A × K_B значений SoftPairScore (для подсветки и предупреждений).</summary>
            public List<List<double>> matrix { get; set; }
            /// <summary>Топ-N наиболее вероятных пар по power-norm ранжированию (см. PairRanking).</summary>
            public List<ScoredPairDto> topPairs { get; set; }
            /// <summary>Почему ушли в picker (для UI: «алгоритм не уверен потому что…»).</summary>
            public string reason { get; set; }
        }

        /// <summary>Одна пара из топ-N: индексы в колонках + покомпонентные скоры + финальный ранг.</summary>
        public sealed class ScoredPairDto
        {
            public int aIdx { get; set; }
            public int bIdx { get; set; }
            public double s1A { get; set; }
            public double s1B { get; set; }
            /// <summary>P_A(a) = S1_A(a)² / Σ S1_A² — нормализованная доля кандидата A.</summary>
            public double pA { get; set; }
            /// <summary>P_B(b) = S1_B(b)² / Σ S1_B².</summary>
            public double pB { get; set; }
            public double p { get; set; }
            public double rank { get; set; }
        }

        // ---------- Главный метод алгоритма ----------

        /// <summary>
        /// Главный метод. Берёт идентификатор любой из двух точек пары, находит
        /// парную (через PersistedRelations), запускает coupled rebind.
        /// </summary>
        [JsonRpcMethod("land/rebindPair", UseSingleObjectParameterDeserialization = true)]
        public Task<CoupledRebindResultDto> RebindPairAsync(CoupledRebindParams p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.anchorId))
                throw new ArgumentException("anchorId is required");

            if (!nodesById.TryGetValue(p.anchorId, out var anyOld) || anyOld == null)
                return Task.FromResult(MakePickerEmpty("Точка не найдена в текущем снимке."));

            // Находим парный якорь через Relations
            var (aOld, bOld) = ResolvePair(anyOld);
            if (aOld == null || bOld == null)
                return Task.FromResult(MakePickerEmpty(
                    "Парный якорь не найден. Эта точка не имеет автопары."));

            // VP-деревья профиль-индексы строятся on-demand из текущего состояния
            // диска (тот же приём, что в UpdateAnchorAsync и ParentRebind).
            // Без этого currentTreesByProfileKey пуст → RebindAnchorTopK
            // возвращает [] → пользователь видит «Нет кандидатов».
            //
            // ВАЖНО: BuildSemanticMarkupFromDisk внутри делает nodesById.Clear()
            // и перезаполняет его свежими якорями с диска. Это убивает старые Id
            // (включая anchorIdA/anchorIdB), которые нам нужны в последующем
            // commitRebindPair. Поэтому сохраняем nodesById перед очисткой и
            // восстанавливаем после построения VP-деревьев — точно как в ParentRebind.
            var savedNodesById = new Dictionary<string, TreeNode>(nodesById, StringComparer.OrdinalIgnoreCase);
            try
            {
                BuildCurrentRebindingForestsFromDisk();
            }
            catch (Exception ex)
            {
                Debug($"[rebindPair] cannot rebuild rebinding forest: {ex.Message}");
                nodesById = savedNodesById;
                return Task.FromResult(MakePickerEmpty(
                    $"Не удалось построить индексы: {ex.Message}"));
            }

            var k = p.k.GetValueOrDefault(K_CANDIDATES);
            var colA = RebindAnchorTopK(aOld, k);
            var colB = RebindAnchorTopK(bOld, k);

            // Возвращаем nodesById обратно к снапшоту со старыми Id — это позволит
            // последующему commitRebindPair найти oldA/oldB по старым anchorIdA/anchorIdB.
            nodesById = savedNodesById;

            return Task.FromResult(Decide(colA, colB, aOld.Id, bOld.Id));
        }

        // ---------- Решающее правило (три порога) ----------

        private CoupledRebindResultDto Decide(
            List<RankedCandidate> colA,
            List<RankedCandidate> colB,
            string anchorIdA,
            string anchorIdB)
        {
            if (colA.Count == 0 || colB.Count == 0)
                return MakePickerEmpty("Нет кандидатов для перепривязки.");

            var failed = new List<string>();
            double marginA, marginB;

            if (colA.Count < 2)
            {
                failed.Add("|C_A|<2 (margin неопределён)");
                marginA = 0.0;
            }
            else
            {
                marginA = colA[0].S1 - colA[1].S1;
                if (marginA < M_LOCAL)
                    failed.Add($"margin_A={marginA:F3}<{M_LOCAL}");
            }

            if (colB.Count < 2)
            {
                failed.Add("|C_B|<2 (margin неопределён)");
                marginB = 0.0;
            }
            else
            {
                marginB = colB[0].S1 - colB[1].S1;
                if (marginB < M_LOCAL)
                    failed.Add($"margin_B={marginB:F3}<{M_LOCAL}");
            }

            var a1 = colA[0].Node;
            var b1 = colB[0].Node;
            var pair = SoftPairScore.Score(a1, b1);
            if (pair < TAU_PAIR)
                failed.Add($"S2={pair:F3}<{TAU_PAIR}");

            if (failed.Count == 0)
            {
                // AUTO-ACCEPT: применяем перепривязку прямо здесь, без round-trip
                // с клиентом через commitRebindPair. a1/b1 — это уже свежие
                // TreeNode с правильными offset'ами из текущего скана файлов
                // (RebindAnchorTopK вернул их через CloneAnchor нодов из
                // currentNodesByProfileKey, который заполняется свежими
                // BuildSemanticMarkupFromDisk).
                if (!string.IsNullOrWhiteSpace(anchorIdA) && !string.IsNullOrWhiteSpace(anchorIdB))
                    ApplyAcceptedPair(anchorIdA, anchorIdB, a1, b1);

                return new CoupledRebindResultDto
                {
                    kind = "accepted",
                    anchorIdA = anchorIdA,
                    anchorIdB = anchorIdB,
                    a = ToClientNodeV2(a1),
                    b = ToClientNodeV2(b1),
                    pairScore = pair,
                    marginA = marginA,
                    marginB = marginB,
                };
            }

            // Picker
            var matrix = BuildMatrix(colA, colB);
            var s1AList = colA.Select(c => c.S1).ToArray();
            var s1BList = colB.Select(c => c.S1).ToArray();
            var topScored = PairRanking.RankTopN(
                s1AList,
                s1BList,
                matrix.Select(r => (IReadOnlyList<double>)r).ToList(),
                topN: 5,
                k: PairRanking.DEFAULT_K,
                tauPair: TAU_PAIR);
            var topPairsDto = topScored.Select(sp => new ScoredPairDto
            {
                aIdx = sp.AIdx,
                bIdx = sp.BIdx,
                s1A = sp.S1A,
                s1B = sp.S1B,
                pA = sp.PA,
                pB = sp.PB,
                p = sp.P,
                rank = sp.Rank,
            }).ToList();

            return new CoupledRebindResultDto
            {
                kind = "picker",
                anchorIdA = anchorIdA,
                anchorIdB = anchorIdB,
                colA = colA.Select(c => new CoupledRebindCandidateDto
                {
                    n = ToClientNodeV2(c.Node),
                    s1 = c.S1,
                }).ToList(),
                colB = colB.Select(c => new CoupledRebindCandidateDto
                {
                    n = ToClientNodeV2(c.Node),
                    s1 = c.S1,
                }).ToList(),
                matrix = matrix,
                topPairs = topPairsDto,
                reason = string.Join("; ", failed),
            };
        }

        private static List<List<double>> BuildMatrix(
            List<RankedCandidate> colA,
            List<RankedCandidate> colB)
        {
            var m = new List<List<double>>(colA.Count);
            foreach (var a in colA)
            {
                var row = new List<double>(colB.Count);
                foreach (var b in colB)
                    row.Add(SoftPairScore.Score(a.Node, b.Node));
                m.Add(row);
            }
            return m;
        }

        /// <summary>
        /// Применяет результат AUTO-ACCEPT прямо на сервере: подменяет старые
        /// якоря (anchorIdA/anchorIdB) свежими TreeNode из RebindAnchorTopK,
        /// пересчитывает Relations, сохраняет snapshot.
        /// Та же логика, что в CommitRebindPairAsync, но без BuildNodeFromTarget —
        /// здесь TreeNode'ы уже готовы (a1Fresh, b1Fresh — клоны из VP-tree).
        /// </summary>
        private void ApplyAcceptedPair(string anchorIdA, string anchorIdB, TreeNode a1Fresh, TreeNode b1Fresh)
        {
            if (a1Fresh == null || b1Fresh == null) return;

            // Старые узлы (нужны для переноса IsManual/Comment)
            nodesById.TryGetValue(anchorIdA, out var oldA);
            nodesById.TryGetValue(anchorIdB, out var oldB);

            var updatedA = CloneAnchor(a1Fresh);
            updatedA.Id = anchorIdA;
            if (oldA != null)
            {
                updatedA.IsManual = oldA.IsManual;
                updatedA.Comment = oldA.Comment;
            }
            updatedA.AnchorFamily = EnsureAnchorFamily(updatedA);

            var updatedB = CloneAnchor(b1Fresh);
            updatedB.Id = anchorIdB;
            if (oldB != null)
            {
                updatedB.IsManual = oldB.IsManual;
                updatedB.Comment = oldB.Comment;
            }
            updatedB.AnchorFamily = EnsureAnchorFamily(updatedB);

            nodesById[anchorIdA] = updatedA;
            nodesById[anchorIdB] = updatedB;

            try
            {
                lock (markupLock)
                {
                    ReplaceAnchorInMarkupUnsafe(updatedA);
                    ReplaceAnchorInMarkupUnsafe(updatedB);
                    RebuildRelationsUnsafe();
                    RebuildMarkupRootsUnsafe();
                    ReloadNodesByIdUnsafe();
                    if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
                        Debug($"[rebindPair/auto-accept] cannot save anchors cache: {err}");
                }
            }
            catch (Exception ex)
            {
                Debug($"[rebindPair/auto-accept] persist failed: {ex.Message}");
            }
        }

        // ---------- Поиск пары через Relations ----------

        /// <summary>
        /// По любой из двух точек пары находит обе. Использует PersistedRelations
        /// (сейчас 1:N с gql-стороны).
        /// </summary>
        private (TreeNode aOld, TreeNode bOld) ResolvePair(TreeNode any)
        {
            if (any == null || currentMarkup?.Relations == null)
                return (null, null);

            // any — это GraphQL поле? тогда пара уже хранится Source=any.Id
            var rel = currentMarkup.Relations.FirstOrDefault(
                r => string.Equals(r.SourceAnchorId, any.Id, StringComparison.OrdinalIgnoreCase));
            if (rel != null && rel.TargetAnchorIds != null && rel.TargetAnchorIds.Count > 0)
            {
                if (nodesById.TryGetValue(rel.TargetAnchorIds[0], out var b))
                    return (any, b);
            }

            // any — это TS резолвер? найдём отношение, где он Target.
            var rel2 = currentMarkup.Relations.FirstOrDefault(
                r => r.TargetAnchorIds != null
                  && r.TargetAnchorIds.Any(t => string.Equals(t, any.Id, StringComparison.OrdinalIgnoreCase)));
            if (rel2 != null && nodesById.TryGetValue(rel2.SourceAnchorId, out var a))
                return (a, any);

            return (null, null);
        }

        private static CoupledRebindResultDto MakePickerEmpty(string reason) =>
            new()
            {
                kind = "picker",
                colA = new List<CoupledRebindCandidateDto>(),
                colB = new List<CoupledRebindCandidateDto>(),
                matrix = new List<List<double>>(),
                reason = reason,
            };

        // ====================================================
        // Commit — применение пользовательского выбора пары
        // ====================================================

        /// <summary>Target одной стороны пары (как ParentRebindTargetDescriptor).</summary>
        public sealed class CoupledRebindTargetDto
        {
            public string filePath { get; set; }
            public int startOffset { get; set; }
            public int endOffset { get; set; }
            /// <summary>Не используется при построении нового якоря (берём по offset),
            /// но клиент шлёт для информативности и валидации.</summary>
            public string anchorKind { get; set; }
        }

        public sealed class CoupledRebindCommitParams
        {
            public string anchorIdA { get; set; }
            public CoupledRebindTargetDto targetA { get; set; }
            public string anchorIdB { get; set; }
            public CoupledRebindTargetDto targetB { get; set; }
        }

        public sealed class CoupledRebindCommitResultDto
        {
            public TreeNodeClientV2 a { get; set; }
            public TreeNodeClientV2 b { get; set; }
            /// <summary>Сообщение об ошибке (если был частичный сбой); пустое при успехе.</summary>
            public string message { get; set; }
        }

        /// <summary>
        /// Применяет пользовательский выбор пары (a*, b*) на сервере.
        /// Для каждой стороны строит новый TreeNode из target (filePath+offset),
        /// замещает якорь в snapshot'е, потом одним блоком пересчитывает
        /// Relations и сохраняет на диск.
        /// </summary>
        [JsonRpcMethod("land/commitRebindPair", UseSingleObjectParameterDeserialization = true)]
        public Task<CoupledRebindCommitResultDto> CommitRebindPairAsync(CoupledRebindCommitParams p)
        {
            if (p == null
                || string.IsNullOrWhiteSpace(p.anchorIdA)
                || string.IsNullOrWhiteSpace(p.anchorIdB)
                || p.targetA == null
                || p.targetB == null)
                throw new ArgumentException("anchorIdA/anchorIdB/targetA/targetB are required");

            if (!nodesById.TryGetValue(p.anchorIdA, out var oldA) || oldA == null)
                return Task.FromResult(new CoupledRebindCommitResultDto
                {
                    message = $"anchorIdA [{p.anchorIdA}] not found in cache; run land/listAnchors again",
                });
            if (!nodesById.TryGetValue(p.anchorIdB, out var oldB) || oldB == null)
                return Task.FromResult(new CoupledRebindCommitResultDto
                {
                    message = $"anchorIdB [{p.anchorIdB}] not found in cache",
                });

            // Строим новые TreeNode из target — переиспользуем существующие
            // TryCreateBindableGqlAnchorAtOffset / TryCreateBindableTsAnchorAtOffset.
            var updatedA = BuildNodeFromTarget(p.targetA, out var msgA);
            if (updatedA == null)
                return Task.FromResult(new CoupledRebindCommitResultDto
                {
                    message = $"Сторона A: {msgA ?? "не удалось построить якорь"}",
                });

            var updatedB = BuildNodeFromTarget(p.targetB, out var msgB);
            if (updatedB == null)
                return Task.FromResult(new CoupledRebindCommitResultDto
                {
                    message = $"Сторона B: {msgB ?? "не удалось построить якорь"}",
                });

            // Сохраняем стабильные id СТАРЫХ якорей — это и есть суть rebind:
            // новый location, но прежний идентификатор → существующие user-links
            // и user-groups продолжают ссылаться.
            updatedA.Id = p.anchorIdA;
            updatedA.IsManual = oldA.IsManual;
            updatedA.Comment = oldA.Comment;
            updatedA.AnchorFamily = EnsureAnchorFamily(updatedA);

            updatedB.Id = p.anchorIdB;
            updatedB.IsManual = oldB.IsManual;
            updatedB.Comment = oldB.Comment;
            updatedB.AnchorFamily = EnsureAnchorFamily(updatedB);

            nodesById[p.anchorIdA] = updatedA;
            nodesById[p.anchorIdB] = updatedB;

            try
            {
                lock (markupLock)
                {
                    ReplaceAnchorInMarkupUnsafe(updatedA);
                    ReplaceAnchorInMarkupUnsafe(updatedB);
                    RebuildRelationsUnsafe();
                    RebuildMarkupRootsUnsafe();
                    ReloadNodesByIdUnsafe();
                    if (!AnchorStore.TrySave(currentFolderPath, currentMarkup, out var err))
                        Debug($"[commitRebindPair] cannot save anchors cache: {err}");
                }
            }
            catch (Exception ex)
            {
                Debug($"[commitRebindPair] persist failed: {ex.Message}");
                return Task.FromResult(new CoupledRebindCommitResultDto
                {
                    a = ToClientNodeV2(updatedA),
                    b = ToClientNodeV2(updatedB),
                    message = $"Применено в памяти, но сохранение упало: {ex.Message}",
                });
            }

            return Task.FromResult(new CoupledRebindCommitResultDto
            {
                a = ToClientNodeV2(updatedA),
                b = ToClientNodeV2(updatedB),
                message = null,
            });
        }

        /// <summary>
        /// Строит TreeNode по filePath + startOffset, делегируя существующему
        /// TryCreateManualAnchorAtOffset (он сам разрулит расширение).
        /// </summary>
        private TreeNode BuildNodeFromTarget(CoupledRebindTargetDto t, out string message)
        {
            message = null;
            if (t == null || string.IsNullOrWhiteSpace(t.filePath))
            {
                message = "пустой filePath";
                return null;
            }
            try
            {
                var node = TryCreateManualAnchorAtOffset(t.filePath, t.startOffset, out message);
                if (node == null)
                    return null;
                if (t.endOffset > 0 && (node.EndOffset ?? -1) != t.endOffset)
                    node.EndOffset = t.endOffset;
                return node;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return null;
            }
        }
    }
}
