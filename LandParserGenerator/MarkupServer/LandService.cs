using Land.Core;
using Land.Core.Parsing;
using Land.Core.Parsing.Tree;
using Land.Core.Specification;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace MarkupServer
{
	public partial class LandService
	{
		// Текущий snapshot разметки (semantic snapshot + Roots), загруженный/созданный для currentFolderPath.
		private PersistedMarkup currentMarkup;
		private readonly object markupLock = new object();

		private BaseParser graphqlParser;
		private BaseParser typescriptParser;
		// Все anchor-узлы, которые мы отдали в дерево. Нужны для updateAnchor.
		private Dictionary<string, TreeNode> nodesById = [];

		// Кэш последнего построенного дерева для папки.
		private string currentFolderPath;
		private readonly Dictionary<string, List<AnchorContext>> currentContextsByProfileKey = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, List<TreeNode>> currentNodesByProfileKey = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, VPTree<AnchorContext>> currentTreesByProfileKey = new(StringComparer.OrdinalIgnoreCase);

		private const string LangGql = "gql";
		private const string LangTs = "ts";

		private const string AnchorFamilyCallable = "callableMember";
		private const string AnchorFamilyRecord = "recordLike";

		private const string AnchorKindGqlField = "gqlField";
		private const string AnchorKindGqlType = "gqlTypeDef";
		private const string AnchorKindGqlInput = "gqlInputDef";
		private const string AnchorKindGqlInterface = "gqlInterfaceDef";
		private const string AnchorKindTs = "tsMember";

		private TreeNodeClientV2 ToClientNodeV2(TreeNode n)
		{
			if (n == null) return null;
			var isAnchor = string.Equals(n.NodeType, "anchor", StringComparison.OrdinalIgnoreCase);

			var c = new TreeNodeClientV2
			{
				id = n.Id,
				n = n.Name,
				t = n.NodeType,
			};

			// Оптимизация трафика: file/offsets нужны только для anchor-нод.
			if (isAnchor)
			{
				c.f = n.Filepath;
				c.s = n.StartOffset;
				c.e = n.EndOffset;
				c.k = n.AnchorKind;

				// Shadow-узлы (anchor в user-группе) несут aid = реальный AnchorId,
				// чтобы клиент мог развести occurrence-Id и canonical-Id.
				if (!string.IsNullOrWhiteSpace(n.RealAnchorId))
					c.aid = n.RealAnchorId;

				// Бейджи для UI — счётчики кросс-якорных связей. Для shadow смотрим на real-id.
				var idForLinks = string.IsNullOrWhiteSpace(n.RealAnchorId) ? n.Id : n.RealAnchorId;
				if (!string.IsNullOrWhiteSpace(idForLinks)
					&& _linkCountByAnchor != null
					&& _linkCountByAnchor.TryGetValue(idForLinks, out var counts))
				{
					if (counts.outCount > 0) c.oc = counts.outCount;
					if (counts.inCount > 0) c.ic = counts.inCount;
				}

				// Auto-pair (gqlField↔ts-резолвер) — даёт префикс ◆ на клиенте.
				// Ставим ТОЛЬКО на canonical (RealAnchorId не задан): это маркер
				// "ядро auto-группы". У shadow в user-папках префикс не нужен —
				// иначе при добавлении ссылки на якорь, который где-то является
				// главным в своей паре, ◆ ехал бы в чужую папку и сбивал смысл.
				var isShadow = !string.IsNullOrWhiteSpace(n.RealAnchorId);
				if (!isShadow
					&& !string.IsNullOrWhiteSpace(idForLinks)
					&& _pairedAnchorIds != null
					&& _pairedAnchorIds.Contains(idForLinks))
				{
					c.pr = true;
				}

				// Системный маркер на самом якоре (lostAnchor — для виртуальных
				// узлов в _Lost-bucket-е и shadow-теней потерянных якорей).
				if (!string.IsNullOrWhiteSpace(n.SystemKind))
					c.sys = n.SystemKind;
			}
			else
			{
				// Group kind: "user" — пользовательская группа; "auto" не пишем (дефолт),
				// чтобы старые клиенты не ломались.
				if (string.Equals(n.GroupKind, "user", StringComparison.OrdinalIgnoreCase))
					c.gk = "user";

				// Системный маркер для специальных групп (_Lost-bucket).
				if (!string.IsNullOrWhiteSpace(n.SystemKind))
					c.sys = n.SystemKind;
			}

			if (n.Children != null && n.Children.Count > 0)
				c.c = n.Children.Select(ToClientNodeV2).Where(x => x != null).ToList();

			return c;
		}

		private List<TreeNodeClientV2> ToClientRootsV2(List<TreeNode> roots)
			=> roots?.Select(ToClientNodeV2).Where(x => x != null).ToList() ?? new List<TreeNodeClientV2>();

		private ListTreeResultV2 MakeListTreeResult(List<TreeNode> roots, bool? fromCache)
			=> new ListTreeResultV2 { r = ToClientRootsV2(roots), fc = fromCache };

		private UpdateAnchorResultV2 MakeUpdateAnchorResult(
			TreeNode updated,
			string parentGroupId = null,
			string parentGroupName = null,
			string fieldGroupId = null,
			string fieldGroupName = null)
			=> new UpdateAnchorResultV2
			{
				u = ToClientNodeV2(updated),
				pg = parentGroupId,
				pn = parentGroupName,
				fg = fieldGroupId,
				fn = fieldGroupName,
			};

		private AddAnchorResultV2 MakeAddAnchorResult(TreeNode node, bool? alreadyExists = null, string message = null)
			=> new AddAnchorResultV2
			{
				a = ToClientNodeV2(node),
				ex = alreadyExists,
				m = message,
			};

		[JsonRpcMethod("shutdown")]
		public Task ShutdownAsync() => Task.CompletedTask;

		[JsonRpcMethod("exit")]
		public void Exit() => Environment.Exit(0);
	}
}
