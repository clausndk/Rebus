﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using NUnit.Framework;
using Rebus.Bus;
using Rebus.Castle.Windsor;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.MongoDb;
using Rebus.Serialization.Json;
using Rebus.Shared;
using Rebus.Tests.Performance.StressMongo.Caf;
using Rebus.Tests.Performance.StressMongo.Caf.Messages;
using Rebus.Tests.Performance.StressMongo.Crm.Messages;
using Rebus.Tests.Performance.StressMongo.Dcc;
using Rebus.Tests.Performance.StressMongo.Legal;
using Rebus.Tests.Performance.StressMongo.Legal.Messages;
using Rebus.Tests.Persistence;
using Rebus.Timeout;
using Rebus.Transports.Msmq;
using System.Linq;
using Shouldly;

namespace Rebus.Tests.Performance.StressMongo
{
    [TestFixture, Category(TestCategories.Integration), Category(TestCategories.Mongo)]
    public class TestStressMongo : MongoDbFixtureBase, IDetermineDestination, IFlowLog
    {
        readonly ConcurrentDictionary<string, ConcurrentQueue<string>> log = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

        readonly Dictionary<Type, string> endpointMappings =
            new Dictionary<Type, string>
                {
                    {typeof (CustomerCreated), GetEndpoint("crm")},
                    {typeof (CustomerCreditCheckComplete), GetEndpoint("caf")},
                    {typeof (CustomerLegallyApproved), GetEndpoint("legal")},
                };

        readonly List<IDisposable> stuffToDispose = new List<IDisposable>();

        IBus crm;
        IBus caf;
        IBus legal;
        IBus dcc;
        TimeoutService timeout;

        protected override void DoSetUp()
        {
            RebusLoggerFactory.Current = new ConsoleLoggerFactory(false) { MinLevel = LogLevel.Warn };

            crm = CreateBus("crm", ContainerAdapterWith("crm"));
            caf = CreateBus("caf", ContainerAdapterWith("caf", typeof(CheckCreditSaga)));
            legal = CreateBus("legal", ContainerAdapterWith("legal", typeof(CheckSomeLegalStuffSaga)));
            dcc = CreateBus("dcc", ContainerAdapterWith("dcc", typeof(MaintainCustomerInformationSaga)));

            // clear saga data collections
            DropCollection("check_credit_sagas");
            DropCollection("check_legal_sagas");
            DropCollection("customer_information_sagas");

            DropCollection("rebus.timeouts");
            timeout = new TimeoutService(new MongoDbTimeoutStorage(ConnectionString, "rebus.timeouts"));
            timeout.Start();

            caf.Subscribe<CustomerCreated>();
            legal.Subscribe<CustomerCreated>();

            dcc.Subscribe<CustomerCreated>();
            dcc.Subscribe<CustomerCreditCheckComplete>();
            dcc.Subscribe<CustomerLegallyApproved>();

            Thread.Sleep(5.Seconds());
        }

        [TestCase(1)]
        [TestCase(100, Ignore = true)]
        public void StatementOfSomething(int count)
        {
            var no = 1;
            count.Times(() => crm.Publish(new CustomerCreated { Name = "John Doe" + no++, CustomerId = Guid.NewGuid() }));

            Thread.Sleep(15.Seconds() + (count * 0.8).Seconds());

            File.WriteAllText("stress-mongo.txt", FormatLogContents());

            var sagas = Collection<CustomerInformationSagaData>("customer_information_sagas");
            var allSagas = sagas.FindAll();

            allSagas.Count().ShouldBe(count);
            allSagas.Count(s => s.CreditStatus.Complete).ShouldBe(count);
            allSagas.Count(s => s.LegalStatus.Complete).ShouldBe(count);
        }

        string FormatLogContents()
        {
            return string.Join(Environment.NewLine + Environment.NewLine, FormatLog(log));
        }

        static IEnumerable<string> FormatLog(ConcurrentDictionary<string, ConcurrentQueue<string>> log)
        {
            return log.Select(
                kvp =>
                string.Format(@"Log for {0}:
{1}", kvp.Key, FormatLog(kvp.Value)));
        }

        static string FormatLog(IEnumerable<string> value)
        {
            return string.Join(Environment.NewLine, value.Select(l => "    " + l));
        }

        protected override void DoTearDown()
        {
            stuffToDispose.ForEach(s =>
                                       {
                                           try
                                           {
                                               s.Dispose();
                                           }
                                           catch
                                           {
                                           }
                                       });

            timeout.Stop();
        }

        IContainerAdapter ContainerAdapterWith(string serviceName, params Type[] types)
        {
            var container = new WindsorContainer();

            foreach (var type in types)
            {
                container.Register(Component.For(GetServices(type)).ImplementedBy(type).LifeStyle.Transient);
            }

            container.Register(Component.For<IFlowLog>().Instance(this));
            //  container.Register(Component.For<IHandleMessages<object>>().Instance(new MessageLogger(this, serviceName)));

            return new WindsorContainerAdapter(container);
        }

        class MessageLogger : IHandleMessages<object>
        {
            readonly IFlowLog flowLog;
            readonly string id;

            public MessageLogger(IFlowLog flowLog, string id)
            {
                this.flowLog = flowLog;
                this.id = id;
            }

            public void Handle(object message)
            {
                flowLog.LogSequence(id, "Received {0}", FormatMessage(message));
            }

            string FormatMessage(object message)
            {
                var name = message.GetType().Name;

                return string.Format("{0}: {1}", name, GetInfo(message));
            }

            string GetInfo(object message)
            {
                if (message is SimulatedCreditCheckComplete)
                {
                    return ((SimulatedCreditCheckComplete)message).CustomerId.ToString();
                }

                if (message is CustomerCreated)
                {
                    var customerCreated = (CustomerCreated)message;

                    return string.Format("{0} {1}", customerCreated.CustomerId, customerCreated.Name);
                }

                if (message is TimeoutReply)
                {
                    var timeoutReply = (TimeoutReply)message;

                    return timeoutReply.CustomData;
                }

                if (message is CustomerCreditCheckComplete)
                {
                    return ((CustomerCreditCheckComplete)message).CustomerId.ToString();
                }

                if (message is SimulatedLegalCheckComplete)
                {
                    return ((SimulatedLegalCheckComplete)message).CustomerId.ToString();
                }

                if (message is CustomerLegallyApproved)
                {
                    return ((CustomerLegallyApproved)message).CustomerId.ToString();
                }

                return "n/a";
            }
        }

        Type[] GetServices(Type type)
        {
            return type.GetInterfaces()
                .Where(i => i.IsGenericType &&
                            (i.GetGenericTypeDefinition() == typeof(IHandleMessages<>)
                             || i.GetGenericTypeDefinition() == typeof(IAmInitiatedBy<>)))
                .ToArray();
        }

        public string GetEndpointFor(Type messageType)
        {
            if (endpointMappings.ContainsKey(messageType))
                return endpointMappings[messageType];

            throw new ArgumentException(string.Format("Cannot determine owner of message type {0}", messageType));
        }

        IBus CreateBus(string serviceName, IContainerAdapter containerAdapter)
        {
            var sagaCollectionName = serviceName + ".sagas";
            var subscriptionsCollectionName = "rebus.subscriptions";

            DropCollection(sagaCollectionName);
            DropCollection(subscriptionsCollectionName);

            var msmqMessageQueue = new MsmqMessageQueue(GetEndpoint(serviceName), "error").PurgeInputQueue();
            MsmqUtil.PurgeQueue("error");

            var sagaPersister = new MongoDbSagaPersister(ConnectionString)
                .SetCollectionName<CheckCreditSagaData>("check_credit_sagas")
                .SetCollectionName<CheckSomeLegalStuffSagaData>("check_legal_sagas")
                .SetCollectionName<CustomerInformationSagaData>("customer_information_sagas");

            var bus = new RebusBus(containerAdapter, msmqMessageQueue, msmqMessageQueue,
                                   new MongoDbSubscriptionStorage(ConnectionString, subscriptionsCollectionName),
                                   sagaPersister, this,
                                   new JsonMessageSerializer(), new TrivialPipelineInspector());

            stuffToDispose.Add(bus);

            containerAdapter.RegisterInstance(bus, typeof(IBus));

            return bus.Start(5);
        }

        static string GetEndpoint(string serviceName)
        {
            return "test.stress.mongo." + serviceName;
        }

        public void LogFlow(Guid correlationId, string message, params object[] objs)
        {
            var key = correlationId.ToString();

            LogSequence(key, message, objs);
        }

        public void LogSequence(string id, string message, params object[] objs)
        {
            Log(id, message, objs);
        }

        void Log(string id, string message, object[] objs)
        {
            log.TryAdd(id, new ConcurrentQueue<string>());

            log[id].Enqueue(string.Format(message, objs));
        }
    }

    /// <summary>
    /// Logs stuff that's happened in relation to a given ID
    /// </summary>
    public interface IFlowLog
    {
        void LogFlow(Guid correlationId, string message, params object[] objs);
        void LogSequence(string id, string message, params object[] objs);
    }
}