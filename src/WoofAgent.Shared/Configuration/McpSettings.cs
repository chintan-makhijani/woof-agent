namespace WoofAgent.Shared.Configuration;

public class McpSettings
{
    public const string SectionName = "McpServers";

    public ZomatoMcpSettings Zomato { get; set; } = new();
    public SwiggyMcpSettings Swiggy { get; set; } = new();
}

public class ZomatoMcpSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Name { get; set; } = "Zomato";
    public bool Enabled { get; set; } = true;
    public string? AccessToken { get; set; }
}

public class SwiggyMcpSettings
{
    public string? AccessToken { get; set; }

    /// <summary>
    /// Token file written by scripts/swiggy_token.py. Takes precedence over AccessToken
    /// and is re-read on change, so a re-login applies without restarting.
    /// </summary>
    public string TokenFile { get; set; } = "~/.woof-agent/swiggy-token.json";

    public string FoodEndpoint { get; set; } = "https://mcp.swiggy.com/food";
    public string InstamartEndpoint { get; set; } = "https://mcp.swiggy.com/im";
    public string DineoutEndpoint { get; set; } = "https://mcp.swiggy.com/dineout";
}
