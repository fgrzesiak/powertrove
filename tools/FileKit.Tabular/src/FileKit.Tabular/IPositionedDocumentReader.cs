namespace FileKit.Tabular;

public interface IPositionedDocumentReader
{
    Task<IReadOnlyList<PositionedPage>> ReadAsync(Stream source, CancellationToken cancellationToken);
}
