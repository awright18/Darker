using System;
using System.Reflection;
using LightInject;
using LightInject.Microsoft.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paramore.Darker.Builder;
using Paramore.Darker.Decorators;
using Paramore.Darker.LightInject;
using Paramore.Darker.Policies;
using Paramore.Darker.QueryLogging;
using Paramore.Darker.RemoteQueries.AwsLambda;
using Paramore.Darker.RemoteQueries.AzureFunctions;
using Paramore.Darker.RemoteQueries.JsonSerialization;
using Polly;
using SampleApi.Domain;
using SampleApi.Ports;
using SampleApi.Ports.Handlers;
using SampleApi.Ports.Queries;
using Serilog;

namespace SampleApi
{
    public class Startup
    {
        internal const string SomethingWentTerriblyWrongCircuitBreakerName = "SomethingWentTerriblyWrongCircuitBreaker";

        public IConfigurationRoot Configuration { get; }

        public Startup(IHostingEnvironment env)
        {
            Configuration = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.secrets.json", optional: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            var container = new ServiceContainer
            {
                ScopeManagerProvider = new StandaloneScopeManagerProvider()
            };

            // Configure and register Serilog.
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.LiterateConsole()
                .CreateLogger();

            container.RegisterInstance(Log.Logger);

            ConfigureDarker(container);

            // Add framework services.
            services.AddMvc();

            return container.CreateServiceProvider(services);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            loggerFactory.AddSerilog();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseMvc();
        }

        private void ConfigureDarker(ServiceContainer container)
        {
            var jsonSerializer = new JsonRemoteQuerySerializer();
            var remoteQueriesConfig = Configuration.GetSection("RemoteQueries");

            var azureQueries = new AzureQueryRegistry(jsonSerializer, remoteQueriesConfig["Azure:baseUri"], remoteQueriesConfig["Azure:functionsKey"]);
            azureQueries.Register<GetRandomQuote, GetRandomQuote.Result>("RandomQuote");
            azureQueries.Register<GetAzureGreeting, string>("DarkerGreeting");

            var awsQueries = new AwsQueryRegistry(jsonSerializer, remoteQueriesConfig["AWS:baseUri"], remoteQueriesConfig["AWS:apiKey"]);
            awsQueries.Register<GetAwsGreeting, string>("darkerHelloWorld");

            // Configure and register Darker.
            var queryProcessor = QueryProcessorBuilder.With()
                .LightInjectHandlers(container, opts => opts
                    .WithQueriesAndHandlersFromAssembly(typeof(GetPeopleHandler).GetTypeInfo().Assembly))
                .RemoteQueries(azureQueries, awsQueries)
                .InMemoryQueryContextFactory()
                .JsonQueryLogging()
                .Policies(ConfigurePolicies())
                .Build();

            container.RegisterInstance(queryProcessor);

            // Don't forget to register the required decorators. todo maybe find a way to auto-discover these
            container.Register(typeof(QueryLoggingDecorator<,>));
            container.Register(typeof(RetryableQueryDecorator<,>));
            container.Register(typeof(FallbackPolicyDecorator<,>));
        }

        private IPolicyRegistry ConfigurePolicies()
        {
            var defaultRetryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(new[]
                {
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(150)
                });

            var circuitBreakerPolicy = Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(1, TimeSpan.FromMilliseconds(500));

            var circuitBreakTheWorstCaseScenario = Policy
                .Handle<SomethingWentTerriblyWrongException>()
                .CircuitBreakerAsync(1, TimeSpan.FromSeconds(5));

            return new PolicyRegistry
            {
                { Paramore.Darker.Policies.Constants.RetryPolicyName, defaultRetryPolicy },
                { Paramore.Darker.Policies.Constants.CircuitBreakerPolicyName, circuitBreakerPolicy },
                { SomethingWentTerriblyWrongCircuitBreakerName, circuitBreakTheWorstCaseScenario },
            };
        }
    }
}
