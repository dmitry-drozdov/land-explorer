using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPTree
{
	public sealed class VpTreeSnapshot
	{
		public string Version = "v1";
		public List<NodeDto> Nodes;     // префиксный порядок
		public List<string> ItemIds;    // Id всех элементов (в порядке твоего списка items)
		public int RootIndex;           // индекс корня в Nodes (обычно 0)
		public string Notes;            // опционально: метаданные
	}

	public sealed class NodeDto
	{
		public string PivotId;          // Id опорной точки
		public double Threshold;        // μ
		public int Left;                // индекс левого дочернего узла в Nodes, -1=нет
		public int Right;               // индекс правого узла, -1=нет
	}

	public sealed partial class VPTree<T>
	{
		// Конструктор для восстановления: НЕ строит, а принимает готовый root
		private VPTree(IList<T> items, Func<T, T, double> distance, Node root, int? seed = null)
		{
			if (items == null || items.Count == 0) throw new ArgumentException("items empty");
			_items = new List<T>(items);
			_dist = distance ?? throw new ArgumentNullException("distance");
			_rng = new Random(seed ?? 42);
			_root = root;
		}

		/// <summary>
		/// Сериализует дерево в снимок. Нужна функция получения Id из элемента.
		/// </summary>
		public VpTreeSnapshot ToSnapshot(Func<T, string> getId, string notes = null)
		{
			if (getId == null) throw new ArgumentNullException("getId");

			var nodes = new List<NodeDto>();
			Func<Node, int> emit = null;
			emit = n =>
			{
				if (n == null) return -1;
				int my = nodes.Count;
				nodes.Add(new NodeDto
				{
					PivotId = getId(_items[n.Index]),
					Threshold = n.Threshold,
					Left = -1,
					Right = -1
				});
				int li = emit(n.Left);
				int ri = emit(n.Right);
				nodes[my].Left = li;
				nodes[my].Right = ri;
				return my;
			};

			int rootIdx = emit(_root);

			var itemIds = new List<string>(_items.Count);
			for (int i = 0; i < _items.Count; i++)
				itemIds.Add(getId(_items[i]));

			return new VpTreeSnapshot
			{
				Nodes = nodes,
				ItemIds = itemIds,
				RootIndex = rootIdx,
				Notes = notes ?? ""
			};
		}

		/// <summary>
		/// Восстанавливает дерево из снимка. Нужны: список элементов, поиск индекса по Id и метрика.
		/// </summary>
		public static VPTree<T> FromSnapshot(
		    VpTreeSnapshot snap,
		    IList<T> items,
		    Func<string, int> indexOfId,
		    Func<T, T, double> distance,
		    int? seed = null)
		{
			if (snap == null) throw new ArgumentNullException("snap");
			if (items == null) throw new ArgumentNullException("items");
			if (indexOfId == null) throw new ArgumentNullException("indexOfId");
			if (distance == null) throw new ArgumentNullException("distance");

			var n = (snap.Nodes != null) ? snap.Nodes.Count : 0;
			if (n == 0) throw new InvalidOperationException("Snapshot has no nodes.");

			// Сначала создаём все узлы, затем связываем ссылки
			var nodeArr = new Node[n];
			for (int i = 0; i < n; i++)
			{
				var dto = snap.Nodes[i];
				int itemIndex = indexOfId(dto.PivotId);
				if (itemIndex < 0 || itemIndex >= items.Count)
					throw new InvalidOperationException("PivotId not found among items: " + dto.PivotId);

				nodeArr[i] = new Node
				{
					Index = itemIndex,
					Threshold = dto.Threshold,
					Left = null,
					Right = null
				};
			}
			for (int i = 0; i < n; i++)
			{
				var dto = snap.Nodes[i];
				if (dto.Left >= 0) nodeArr[i].Left = nodeArr[dto.Left];
				if (dto.Right >= 0) nodeArr[i].Right = nodeArr[dto.Right];
			}

			var root = nodeArr[snap.RootIndex];
			return new VPTree<T>(items, distance, root, seed);
		}
	}
}
