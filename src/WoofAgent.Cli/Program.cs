#pragma warning disable SKEXP0001 // AsKernelFunction is experimental

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using WoofAgent.Core.Agents;
using WoofAgent.Core.Filters;
using WoofAgent.Core.Plugins;
using WoofAgent.Providers;
using WoofAgent.Shared.Auth;
using WoofAgent.Shared.Configuration;

// Parse command line args
var useA2UI = !args.Contains("--text");
var useZomato = args.Contains("--zomato");
var useSwiggy = args.Contains("--swiggy");
var useSwiggyInstamart = args.Contains("--swiggy-instamart");
var useSwiggyDineout = args.Contains("--swiggy-dineout");
var useWasher = args.Contains("--washer");
var useAnySwiggy = useSwiggy || useSwiggyInstamart || useSwiggyDineout;
var useMcp = useZomato || useAnySwiggy;

// Build configuration
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>(optional: true)
    .Build();

// Bind settings
var llmSettings = new LlmSettings();
configuration.GetSection(LlmSettings.SectionName).Bind(llmSettings);

// Build kernel with provider
var kernelBuilder = Kernel.CreateBuilder();
kernelBuilder.AddLlmProvider(llmSettings);

IMcpClient? mcpClient = null;

if (useMcp)
{
    var mcpSettings = new McpSettings();
    configuration.GetSection(McpSettings.SectionName).Bind(mcpSettings);

    string endpoint;
    string name;
    string? accessToken = null;
    FileTokenStore? swiggyTokenStore = null;

    if (useZomato)
    {
        endpoint = mcpSettings.Zomato.Endpoint;
        name = mcpSettings.Zomato.Name;
        accessToken = mcpSettings.Zomato.AccessToken;
    }
    else
    {
        swiggyTokenStore = new FileTokenStore(mcpSettings.Swiggy.TokenFile, mcpSettings.Swiggy.AccessToken);
        if (useSwiggyInstamart)
        {
            endpoint = mcpSettings.Swiggy.InstamartEndpoint;
            name = "SwiggyInstamart";
        }
        else if (useSwiggyDineout)
        {
            endpoint = mcpSettings.Swiggy.DineoutEndpoint;
            name = "SwiggyDineout";
        }
        else
        {
            endpoint = mcpSettings.Swiggy.FoodEndpoint;
            name = "SwiggyFood";
        }
    }

    // Set up SSE transport with optional auth token
    var transportOptions = new SseClientTransportOptions
    {
        Endpoint = new Uri(endpoint),
        Name = name
    };

    SseClientTransport transport;
    if (swiggyTokenStore is not null)
    {
        // Swiggy tokens last 5 days with no refresh token; read the token per request
        // so a re-login (scripts/swiggy_token.py login) applies without a restart
        ReportTokenStatus(name, swiggyTokenStore);
        var httpClient = new HttpClient(new BearerTokenHandler(swiggyTokenStore, name));
        transport = new SseClientTransport(transportOptions, httpClient, ownsHttpClient: true);
    }
    else
    {
        if (!string.IsNullOrEmpty(accessToken))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}"
            };
        }

        transport = new SseClientTransport(transportOptions);
    }

    // Connect to MCP server and discover tools
    Console.WriteLine($"Connecting to {name} MCP server at {endpoint}...");
    mcpClient = await McpClientFactory.CreateAsync(transport);
    var tools = await mcpClient.ListToolsAsync();
    Console.WriteLine($"Discovered {tools.Count} {name} tools.");

    // Register MCP tools as SK kernel functions
    kernelBuilder.Plugins.AddFromFunctions(name,
        tools.Select(t => t.AsKernelFunction()));

    // Register chaining filter for Swiggy Food MCP tools
    // Chains: search_restaurants→get_restaurant_menu, add_item_to_cart→get_cart_summary+get_delivery_eta, place_order→get_order_status
    if (useAnySwiggy)
    {
        var mcpPluginName = name; // capture for closure
        kernelBuilder.Services.AddSingleton<IAutoFunctionInvocationFilter>(
            _ => new SwiggyChainingFilter(mcpPluginName));
    }
}
else if (useWasher)
{
    // Register washer + shopping plugins for tool chaining demo
    kernelBuilder.Plugins.AddFromType<WasherPlugin>();
    kernelBuilder.Plugins.AddFromType<ShoppingPlugin>();

    // Register the chaining filter: schedule_washing automatically triggers check_detergent
    kernelBuilder.Services.AddSingleton<IAutoFunctionInvocationFilter, WasherChainingFilter>();
}
else
{
    // Register local fridge plugin for non-MCP modes
    kernelBuilder.Plugins.AddFromType<FridgePlugin>();
}

var kernel = kernelBuilder.Build();

// Create agent based on mode
IAgent agent;
if (useZomato)
{
    agent = new ZomatoAgent(kernel);
}
else if (useAnySwiggy)
{
    var service = useSwiggyInstamart ? SwiggyService.Instamart
        : useSwiggyDineout ? SwiggyService.Dineout
        : SwiggyService.Food;
    agent = new SwiggyAgent(kernel, service);
}
else if (useWasher)
{
    agent = new SmartWasherAgent(kernel);
}
else if (useA2UI)
{
    agent = new A2UIFridgeAgent(kernel);
}
else
{
    agent = new SmartFridgeAgent(kernel);
}

// CLI header
Console.WriteLine($"Woof Agent CLI - {agent.Name}");
Console.WriteLine($"Provider: {llmSettings.DefaultProvider} ({GetModelId(llmSettings)})");
if (useMcp)
    Console.WriteLine($"Mode: {agent.Name} (MCP)");
else
    Console.WriteLine(useA2UI ? "Mode: A2UI (use --text for plain text)" : "Mode: Plain text");
Console.WriteLine("Type your prompt (or 'exit' to quit):");
Console.WriteLine();

try
{
    while (true)
    {
        Console.Write("> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;

        try
        {
            var response = await agent.InvokeAsync(input);
            Console.WriteLine();

            if (useA2UI && !useMcp)
            {
                // Parse and display A2UI response
                var (text, json) = A2UIFridgeAgent.ParseResponse(response);

                Console.WriteLine("📝 Response:");
                Console.WriteLine(text);

                if (!string.IsNullOrEmpty(json))
                {
                    Console.WriteLine();
                    Console.WriteLine("🎨 A2UI JSON:");
                    Console.WriteLine("─".PadRight(50, '─'));
                    Console.WriteLine(json);
                    Console.WriteLine("─".PadRight(50, '─'));
                }
            }
            else
            {
                Console.WriteLine(response);
            }

            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine();
        }
    }
}
finally
{
    // Dispose MCP client if created
    if (mcpClient is not null)
    {
        await mcpClient.DisposeAsync();
    }
}

Console.WriteLine("Goodbye!");

static void ReportTokenStatus(string name, FileTokenStore store)
{
    var token = store.GetCurrent();
    if (token is null)
    {
        Console.WriteLine($"[{name}] No token found in {store.FilePath} or user-secrets. Run: python3 scripts/swiggy_token.py login");
        return;
    }

    if (token.Remaining is not { } remaining)
        Console.WriteLine($"[{name}] Token loaded (expiry unknown).");
    else if (token.IsExpired)
        Console.WriteLine($"[{name}] WARNING: token expired at {token.ExpiresAt:u}. Run: python3 scripts/swiggy_token.py login");
    else if (remaining < TimeSpan.FromHours(24))
        Console.WriteLine($"[{name}] WARNING: token expires in {remaining.TotalHours:F1}h ({token.ExpiresAt:u}). Re-login soon.");
    else
        Console.WriteLine($"[{name}] Token valid for {remaining.TotalDays:F1} more days ({token.ExpiresAt:u}).");
}

static string GetModelId(LlmSettings settings) =>
    settings.DefaultProvider.ToLowerInvariant() switch
    {
        "openai" => settings.OpenAi.ModelId,
        "gemini" => settings.Gemini.ModelId,
        _ => "unknown"
    };
