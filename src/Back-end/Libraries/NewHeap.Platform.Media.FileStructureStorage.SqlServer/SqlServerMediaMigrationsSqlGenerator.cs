using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal sealed class SqlServerMediaMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    ICommandBatchPreparer commandBatchPreparer) : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        var schema = FileStructureModelCacheKeyFactory.GetSchema(Dependencies.CurrentContext.Context);

        FileStructureMigrationSchema.Route(operations, schema, "[nhmedia]", Dependencies.SqlGenerationHelper);

        return base.Generate(operations, model, options);
    }
}
