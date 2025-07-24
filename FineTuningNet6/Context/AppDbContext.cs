using FineTuningNet6.Models;
using iText.Commons.Actions.Contexts;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FineTuningNet6.Context
{
    public class AppDbContext : DbContext
    {
        public DbSet<GptImageResult> GptImageResults { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseNpgsql("Host=185.100.67.39; Database=uchetai_db; Username=postgres; Password=QWEqwe123@");
        }
    }
}
