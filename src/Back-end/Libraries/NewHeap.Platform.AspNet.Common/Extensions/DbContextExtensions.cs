using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Extensions;

public static class DbContextExtensions
{
    /// <summary>
    /// Configures the token validation parameters for JWT bearer authentication
    /// </summary>
    /// <param name="cfg"></param>
    /// <param name="configuration"></param>
    public static void Temp(this DbContext context,
        IConfiguration configuration)
    {
        
    }
}