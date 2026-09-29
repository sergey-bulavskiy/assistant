using Assistant.Application.Families;

namespace Assistant.Infrastructure.Families;

public class CurrentFamily : ICurrentFamily
{
    public long? FamilyId { get; private set; }

    public void Set(long? familyId) => FamilyId = familyId;
}
