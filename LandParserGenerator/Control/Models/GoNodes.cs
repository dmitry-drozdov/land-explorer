using Land.Core.Parsing.Tree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	public class GoFuncNode
	{
		public Node Node { get; set; }
		public GoFuncNode(Node node)
		{
			Node = node;
		}	
	}
	public class GoTypeNode
	{
		public Node Node { get; set; }
		public GoType GoType { get; set; }
		public GoTypeNode(Node node, GoType goType)
		{
			Node = node;
			GoType = goType;
		}	
	}
	public class GoNodes
	{
		public LinkedList<GoFuncNode> Funcs { get; set; }
		public LinkedList<GoTypeNode> Types { get; set; }
	}
}
