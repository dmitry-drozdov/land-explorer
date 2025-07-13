using Jaeger.Samplers;
using Jaeger;
using OpenTracing.Util;
using OpenTracing;
using Jaeger.Reporters;
using Jaeger.Senders.Thrift;

namespace Land.Control
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