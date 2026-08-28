namespace VoxOthers.Contracts;

/// <summary>
/// Natureza do atendimento. Define o que o Runtime espera encontrar no
/// registro e o que ele precisa produzir.
/// </summary>
public enum MediaKind
{
    /// <summary>Atendimento de voz. Exige um arquivo de áudio.</summary>
    Call = 0,

    /// <summary>Atendimento de texto (chat, e-mail, mensageria).</summary>
    Chat = 1
}
