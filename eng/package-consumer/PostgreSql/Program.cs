using System;
using Doka.EntityFrameworkCore.SafeMigrations;
using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;
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

_ = new DbContextOptionsBuilder().UsePostgreSqlSafeMigrations();

IServiceCollection services = new ServiceCollection();
services.AddPostgreSqlSafeMigrations();

Console.WriteLine("SafeMigrations PostgreSQL package consumer verified.");
return 0;
