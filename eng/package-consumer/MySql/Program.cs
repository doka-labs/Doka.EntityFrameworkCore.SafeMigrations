using System;
using Doka.EntityFrameworkCore.SafeMigrations;
using Doka.EntityFrameworkCore.SafeMigrations.MySql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

var migrationBuilder = new MigrationBuilder("PackageConsumer");
migrationBuilder.EnsureSchemaExists("consumer_schema");

if (migrationBuilder.Operations.Count != 1
    || migrationBuilder.Operations[0] is not SafeMigrationOperation)
{
    return 1;
}

_ = new DbContextOptionsBuilder().UseMySqlSafeMigrations();

IServiceCollection services = new ServiceCollection();
services.AddEntityFrameworkDokaMySqlSafeMigrations();

Console.WriteLine("SafeMigrations MySQL/MariaDB package consumer verified.");
return 0;
