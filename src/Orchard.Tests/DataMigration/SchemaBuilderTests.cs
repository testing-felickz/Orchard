﻿using System;
using System.Data;
using System.Linq;
using Autofac;
using NHibernate;
using NUnit.Framework;
using Orchard.Data;
using Orchard.Data.Migration.Interpreters;
using Orchard.Data.Migration.Schema;
using Orchard.Data.Providers;
using Orchard.Environment;
using Orchard.Environment.Configuration;
using Orchard.Environment.ShellBuilders.Models;
using Orchard.FileSystems.AppData;
using Orchard.Reports.Services;
using Orchard.Tests.ContentManagement;
using System.IO;
using Orchard.Tests.Environment;
using Orchard.Tests.FileSystems.AppData;
using Orchard.Tests.Stubs;

namespace Orchard.Tests.DataMigration {
    [TestFixture]
    public class SchemaBuilderTests {
        private IContainer _container;
        private ISessionFactory _sessionFactory;
        private string _databaseFileName;
        private string _tempFolder;
        private SchemaBuilder _schemaBuilder;
        private DefaultDataMigrationInterpreter _interpreter;
        private ISession _session;

        [SetUp]
        public void Setup() {
            _databaseFileName = Path.GetTempFileName();
            _sessionFactory = DataUtility.CreateSessionFactory(_databaseFileName);

            _tempFolder = Path.GetTempFileName();
            File.Delete(_tempFolder);
            var appDataFolder = AppDataFolderTests.CreateAppDataFolder(_tempFolder);

            var builder = new ContainerBuilder();

            _session = _sessionFactory.OpenSession();
            builder.RegisterInstance(appDataFolder).As<IAppDataFolder>();
            builder.RegisterType<SqlCeDataServicesProvider>().As<IDataServicesProvider>();
            builder.RegisterType<DataServicesProviderFactory>().As<IDataServicesProviderFactory>();
            builder.RegisterType<StubReportsCoordinator>().As<IReportsCoordinator>();
            builder.RegisterType<DefaultDataMigrationInterpreter>().As<IDataMigrationInterpreter>();
            builder.RegisterType<SessionConfigurationCache>().As<ISessionConfigurationCache>();
            builder.RegisterType<SessionFactoryHolder>().As<ISessionFactoryHolder>();
            builder.RegisterType<StubHostEnvironment>().As<IHostEnvironment>();
            builder.RegisterInstance(new DefaultContentManagerTests.TestSessionLocator(_session)).As<ISessionLocator>();
            builder.RegisterInstance(new ShellBlueprint { Records = Enumerable.Empty<RecordBlueprint>() }).As<ShellBlueprint>();
            builder.RegisterInstance(new ShellSettings { Name = "temp", DataProvider = "SqlCe", DataTablePrefix = "TEST" }).As<ShellSettings>();
            builder.RegisterModule(new DataModule());
            _container = builder.Build();

            _interpreter = _container.Resolve<IDataMigrationInterpreter>() as DefaultDataMigrationInterpreter;
            _schemaBuilder = new SchemaBuilder(_interpreter);
        }

        [Test]
        public void AllMethodsShouldBeCalledSuccessfully() {

            _schemaBuilder = new SchemaBuilder(new NullInterpreter());

            _schemaBuilder
                .CreateTable("User", table => table
                    .ContentPartRecord()
                    .Column("Firstname", DbType.String, column => column.WithLength(255))
                    .Column("Lastname", DbType.String, column => column.WithPrecision(0).WithScale(1)))
                .CreateTable("Address", table => table
                    .ContentPartVersionRecord()
                    .Column("City", DbType.String)
                    .Column("ZIP", DbType.Int32, column => column.Unique())
                    .Column("UserId", DbType.Int32, column => column.NotNull()))
                .CreateForeignKey("User_Address", "User", new[] { "UserId" }, "User", new[] { "Id" })
                .AlterTable("User", table => table
                    .AddColumn("Age", DbType.Int32))
                .AlterTable("User", table => table
                    .DropColumn("Lastname"))
                .AlterTable("User", table => table
                    .CreateIndex("IDX_XYZ", "NickName"))
                .AlterTable("User", table => table
                    .DropIndex("IDX_XYZ"))
                .DropForeignKey("Address", "User_Address")
                .DropTable("Address")
                .ExecuteSql("drop database", statement => statement.ForProvider("SqlCe"))
                .ExecuteSql("DROP DATABASE", statement => statement.ForProvider("SQLServer"));
        }

        [Test]
        public void CreateCommandShouldBeHandled() {

            _schemaBuilder
                .CreateTable("User", table => table
                    .Column("Id", DbType.Int32, column => column.PrimaryKey().Identity())
                    .Column("Firstname", DbType.String, column => column.WithLength(255))
                    .Column("Lastname", DbType.String, column => column.WithLength(100).NotNull())
                    .Column("SN", DbType.AnsiString, column => column.WithLength(40).Unique())
                    .Column("Salary", DbType.Decimal, column => column.WithPrecision(9).WithScale(2))
                    .Column("Gender", DbType.Decimal, column => column.WithDefault(""))
                    );
        }

        [Test]
        public void DropTableCommandShouldBeHandled() {
            _schemaBuilder
                .CreateTable("User", table => table
                    .Column("Id", DbType.Int32, column => column.PrimaryKey().Identity())
                    .Column("Firstname", DbType.String, column => column.WithLength(255))
                    .Column("Lastname", DbType.String, column => column.WithLength(100).NotNull())
                    .Column("SN", DbType.AnsiString, column => column.WithLength(40).Unique())
                    .Column("Salary", DbType.Decimal, column => column.WithPrecision(9).WithScale(2))
                    .Column("Gender", DbType.Decimal, column => column.WithDefault(""))
                    );

            _schemaBuilder
                .DropTable("User");
        }

        [Test]
        public void CustomSqlStatementsShouldBeHandled() {

            _schemaBuilder
                .ExecuteSql("select 1");
        }

        [Test]
        public void AlterTableCommandShouldBeHandled() {

            _schemaBuilder
                .CreateTable("User", table => table
                    .Column("Firstname", DbType.String, column => column.WithLength(255))
                    .Column("Lastname", DbType.String, column => column.WithLength(100).NotNull()))
                .AlterTable("User", table => table
                    .AddColumn("Age", DbType.Int32))
                .AlterTable("User", table => table
                    .AlterColumn("Lastname", column => column.WithDefault("Doe")))
                .AlterTable("User", table => table
                    .DropColumn("Firstname")
                );

            // creating a new row should assign a default value to Firstname and Age
            _schemaBuilder
                .ExecuteSql("insert into TEST_User VALUES (DEFAULT, DEFAULT)");

            // ensure wehave one record woth the default value
            var command = _session.Connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM TEST_User WHERE Lastname = 'Doe'";
            Assert.That(command.ExecuteScalar(), Is.EqualTo(1));

            // ensure this is not a false positive
            command = _session.Connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM TEST_User WHERE Lastname = 'Foo'";
            Assert.That(command.ExecuteScalar(), Is.EqualTo(0));
        }

        [Test]
        public void ForeignKeyShouldBeCreatedAndRemoved() {

            _schemaBuilder
                .CreateTable("User", table => table
                    .Column("Id", DbType.Int32, column => column.PrimaryKey().Identity())
                    .Column("Firstname", DbType.String, column => column.WithLength(255))
                    .Column("Lastname", DbType.String, column => column.WithPrecision(0).WithScale(1)))
                .CreateTable("Address", table => table
                    .Column("City", DbType.String)
                    .Column("ZIP", DbType.Int32, column => column.Unique())
                    .Column("UserId", DbType.Int32, column => column.NotNull()))
                .CreateForeignKey("FK_User", "Address", new[] { "UserId" }, "User", new[] { "Id" })
                .DropForeignKey("Address", "FK_User");
        }

        [Test, ExpectedException]
        public void BiggerDataShouldNotFit() {
            _schemaBuilder
                .CreateTable("ContentItemRecord", table => table
                    .Column("Id", DbType.Int32, column => column.PrimaryKey().Identity())
                    .Column("Data", DbType.String, column => column.WithLength(255)));

            // should write successfully less than 255 chars
            _schemaBuilder
                .ExecuteSql("insert into TEST_ContentItemRecord (Data) values('Hello World')");

            // should throw an exception if trying to write more data
            _schemaBuilder
                .ExecuteSql(String.Format("insert into TEST_ContentItemRecord (Data) values('{0}')", new String('x', 256)));

            _schemaBuilder
                .AlterTable("ContentItemRecord", table => table
                    .AlterColumn("Data", column => column.WithType(DbType.String).WithLength(257)));

            _schemaBuilder
                .ExecuteSql(String.Format("insert into TEST_ContentItemRecord (Data) values('{0}')", new String('x', 256)));
        }

        [Test]
        public void ShouldAllowFieldSizeAlteration() {
            _schemaBuilder
                .CreateTable("ContentItemRecord", table => table
                    .Column("Id", DbType.Int32, column => column.PrimaryKey().Identity())
                    .Column("Data", DbType.String, column => column.WithLength(255)));

            // should write successfully less than 255 chars
            _schemaBuilder
                .ExecuteSql("insert into TEST_ContentItemRecord (Data) values('Hello World')");

            _schemaBuilder
                .AlterTable("ContentItemRecord", table => table
                    .AlterColumn("Data", column => column.WithType(DbType.String).WithLength(2048)));

            // should write successfully a bigger value now
            _schemaBuilder
                .ExecuteSql(String.Format("insert into TEST_ContentItemRecord (Data) values('{0}')", new String('x', 2048)));
        }

        [Test, ExpectedException(typeof(OrchardException))]
        public void ChangingSizeWithoutTypeShouldNotBeAllowed() {
            _schemaBuilder
                .CreateTable("ContentItemRecord", table => table
                    .Column("Id", DbType.Int32, column => column.PrimaryKey().Identity())
                    .Column("Data", DbType.String, column => column.WithLength(255)));

            _schemaBuilder
                .AlterTable("ContentItemRecord", table => table
                    .AlterColumn("Data", column => column.WithLength(2048)));

        }
    }
}