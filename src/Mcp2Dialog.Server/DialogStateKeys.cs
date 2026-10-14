using Microsoft.AspNetCore.DataProtection;

namespace Mcp2Dialog.Server;

public static class DialogStateKeys
{
    /// <summary>
    /// The keys that encrypt requestState. Any instance must be able to decrypt a token another instance minted,
    /// so every instance needs the same application name and the same key ring: that's the one real prerequisite
    /// for running this stateless server behind a load balancer without sticky sessions.
    /// </summary>
    public static IServiceCollection AddDialogStateKeys(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, ServerInstance instance)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("Mcp2Dialog");

        if (instance.IsolatedKeys)
        {
            // Demo switch: keys that exist only in this process, so tokens from the other instance fail to decrypt.
            dataProtection.UseEphemeralDataProtectionProvider();
        }
        else if (configuration["DataProtection:KeyRingPath"] is { Length: > 0 } path)
        {
            // The shared key ring: a directory every instance reads and writes. In production this would be a
            // shared store (blob storage, Redis, a database) with the keys protected at rest.
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(path, environment.ContentRootPath)));
        }

        return services;
    }
}
