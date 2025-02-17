using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebAPI.DAL.Entities;

namespace WebAPI.DAL
{
    public class AppDbContext : NhDbContext
    {
        public DbSet<Address> Logs { get; set; }

        public AppDbContext()
            : base()
        {
        }

        public AppDbContext(DbContextOptions contextOptions)
            : base(contextOptions)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
        }
    }
}