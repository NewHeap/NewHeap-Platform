using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.DAL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace WebAPI.DAL
{
    class AppDbContextFactory : NhDbContextFactory<AppDbContext>
    {
        public override AppDbContext CreateDbContext(string[] args)
        {
            var builder = CreateBuilder();

            return new AppDbContext(builder.Options);
        }
    }
}
