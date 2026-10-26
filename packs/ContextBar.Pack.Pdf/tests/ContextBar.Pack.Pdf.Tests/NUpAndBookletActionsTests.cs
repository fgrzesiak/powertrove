
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContextBar.Abstractions;
using ContextBar.Abstractions.Files;
using ContextBar.Abstractions.Testing;
using ContextBar.Infrastructure.Files;
using FileKit.Pdf;
using Xunit;

namespace ContextBar.Pack.Pdf.Tests;

public class NUpActionTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    private readonly FileActionServices _services;

    public NUpActionTests()
    {
        _services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
    }

    private string CreateFixture()
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linked-objstm.pdf"));
        return _dir.Write("input.pdf", pdf);
    }

    protected override IAction CreateAction() => new NUpPdfAction(_services);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(CreateFixture());
}

public class BookletActionTests : ActionContractTestsBase, IDisposable
{
    private readonly TempDirectory _dir = new();
    public void Dispose() => _dir.Dispose();

    private readonly FileActionServices _services;

    public BookletActionTests()
    {
        _services = FileActionServices.CreatePhysical(new FileSignatureVerifier());
    }

    private string CreateFixture()
    {
        byte[] pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linked-objstm.pdf"));
        return _dir.Write("input.pdf", pdf);
    }

    protected override IAction CreateAction() => new BookletPdfAction(_services);
    protected override ContextSnapshot CreateValidSnapshot() => Snapshots.ForPaths(CreateFixture());
}
