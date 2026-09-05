using PassCrate.Core.Legal;

namespace PassCrate.App.Views;

public partial class LegalDocumentPage : ContentPage, IQueryAttributable
{
    public LegalDocumentPage()
    {
        InitializeComponent();
        BindingContext = LegalDocuments.Terms;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var documentId = query.TryGetValue("document", out var value)
            ? Uri.UnescapeDataString(Convert.ToString(value) ?? string.Empty)
            : string.Empty;
        var document = LegalDocuments.Get(documentId);
        BindingContext = document;
        Title = document.Title;
    }
}
