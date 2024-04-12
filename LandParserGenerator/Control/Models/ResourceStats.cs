using Land.Markup;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Land.Control.Models
{
	internal class ResourceStats
	{
		public float ParseGraphql;
		public float AddGraphqlConcern;
		public float ParseGoTotalLib;
		public float ParseGoTotalLibOutside;
		public float ParseGoTotal;
		public float ParseGoLoadText;
		public float ParseGoLog;
		public float VisitGo;
		public float AddGoConcern;
		private Stopwatch watch;


		public void Start()
		{
			watch = Stopwatch.StartNew();
		}
		public void Stop(ref float val)
		{
			watch.Stop();
			val += watch.ElapsedMilliseconds;
		}

		public override string ToString()
		{
			return $"graphql [parse: {Format(ParseGraphql)}, add concern: {Format(AddGraphqlConcern)}] " +
				$"go [parse {Format(ParseGoTotal)}=LIB:{Format(ParseGoTotalLib)}({Format(ParseGoTotalLibOutside)})+IO:{Format(ParseGoLoadText)}+LOG:{Format(ParseGoLog)}, " +
					$"visit {Format(VisitGo)}, add concern {Format(AddGoConcern)}] ";
		}

		private string Format(float val)
		{
			if (val >= 1000)
			{
				return $"{val / 1000: 0.0} s";
			}
			return $"{val / 1000: 0.00} s";
		}
	}
}
