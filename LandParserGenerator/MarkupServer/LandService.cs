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
		// Текущий snapshot разметки (Roots), загруженный/созданный для currentFolderPath.
		// Нужен, чтобы updateAnchor мог обновлять файл на диске.
		private PersistedMarkup currentMarkup;
		private readonly object markupLock = new object();

		private BaseParser graphqlParser;
		private BaseParser typescriptParser;
		// Все узлы, которые мы отдали в дерево (anchor'ы нужны для updateAnchor).
		// ВАЖНО: ключ должен быть глобально уникальным на весь folder (иначе коллизии при нескольких файлах).
		private Dictionary<string, TreeNode> nodesById = [];

		// Кэш последнего построенного дерева для папки (чтобы updateAnchor работал на весь folder,
		// и не было жёстко зашитых путей).
		private string currentFolderPath;
		private List<MethodAnchor> currentGqlAnchors = new();
		private List<TreeNode> currentGqlNodes = new();
		private VPTree<MethodAnchor> currentTree;
		private Dist.Weights currentWeights = new();


		private static TreeNodeClientV2 ToClientNodeV2(TreeNode n)
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
			}

			if (n.Children != null && n.Children.Count > 0)
				c.c = n.Children.Select(ToClientNodeV2).Where(x => x != null).ToList();

			return c;
		}

		private static List<TreeNodeClientV2> ToClientRootsV2(List<TreeNode> roots)
			=> roots?.Select(ToClientNodeV2).Where(x => x != null).ToList() ?? new List<TreeNodeClientV2>();

		private static ListTreeResultV2 MakeListTreeResult(List<TreeNode> roots, bool? fromCache)
			=> new ListTreeResultV2 { r = ToClientRootsV2(roots), fc = fromCache };

		private static UpdateAnchorResultV2 MakeUpdateAnchorResult(TreeNode updated)
			=> new UpdateAnchorResultV2 { u = ToClientNodeV2(updated) };

		[JsonRpcMethod("shutdown")]
		public Task ShutdownAsync() => Task.CompletedTask;

		[JsonRpcMethod("exit")]
		public void Exit() => Environment.Exit(0);
	}
}
