using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	public abstract class GoType
	{
		public string Name { get; }
		public GoType(string name)
		{
			Name = name;
		}
	}

	public class SimpleType : GoType
	{
		public SimpleType(string name) : base(name)
		{
		}
	}
	public class StructType : GoType
	{
		public StructType(string name) : base(name)
		{
			Fields = new List<GoType>();
		}

		public List<GoType> Fields { get; set; }
	}
}
