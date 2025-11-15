using Jaeger.Reporters;
using Jaeger.Samplers;
using Jaeger.Senders.Thrift;
using Jaeger;
using OpenTracing.Util;
using OpenTracing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarkupServer
{
	public static class Tracing
	{
		public static void Init()
		{
			if (GlobalTracer.IsRegistered())
				return;

			var sender = new UdpSender("localhost", 6831, 0);
			var reporter = new RemoteReporter.Builder()
			    .WithSender(sender)
			    .Build();

			var tracer = new Tracer.Builder("MyLegacyApp")
			    .WithReporter(reporter)
			    .WithSampler(new ConstSampler(true))
			    .Build();


			GlobalTracer.Register(tracer);
		}

		public static ITracer Tracer => GlobalTracer.Instance;
	}
}
