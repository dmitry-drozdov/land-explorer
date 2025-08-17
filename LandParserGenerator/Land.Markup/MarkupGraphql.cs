using Land.Core.Parsing.Tree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPTree;

namespace Land.Markup
{
	public class MarkupGraphql
	{
		private List<MethodAnchor> _anchors = new List<MethodAnchor>();
		private Dictionary<Guid, MethodAnchor> _anchorByConcernId = new Dictionary<Guid, MethodAnchor>();
		private Rebinder _rebinder;
		public MarkupGraphql() { }

		public void AddAnchor(Node n, Guid concernId)
		{
			var typeName = n.Parent.Children[1].ToString().Replace("id: ", "");
			var name = n.Children.First().ToString().Replace("id: ", "");

			var args = n.Children.Skip(1).
				TakeWhile(x => x.Type == "func_arg").
				Select(x => new Tuple<string, string>(
					x.Children[0].Children[1].Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", ""), // type
					x.Children[0].Children[0].ToString().Replace("id: ", "") // name
				));

			var returnType = n.Children.Last().Children.First(y => y.ToString() != "LSB: [").ToString().Replace("id: ", "");

			var anchor = MethodAnchor.FromRaw(n.Id.ToString(), name, args, new List<string> { returnType }, typeName);
			_anchors.Add(anchor);
			_anchorByConcernId[concernId] = anchor;
		}

		public void CreateRebinder()
		{
			var weights = new Dist.Weights();
			_rebinder = new Rebinder(_anchors, weights);
			AnchorsIO.SaveJson("anchors.json", _anchors);
		}
	}
}
