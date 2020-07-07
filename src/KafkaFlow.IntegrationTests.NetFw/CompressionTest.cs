namespace KafkaFlow.IntegrationTests.NetFw
{
    using System.Threading.Tasks;
    using KafkaFlow.IntegrationTests.Common;
    using KafkaFlow.IntegrationTests.Common.Core.Producers;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Producers;

    [TestClass]
    public class CompressionTest : BaseCompressionTests
    {
        [TestInitialize]
        public void Setup()
        {
            base.Setup();
        }

        [TestMethod]
        public Task GzipTest()
        {
            return base.GzipTest(Bootstrapper.GetService<IMessageProducer<GzipProducer>>());
        }
    }
}