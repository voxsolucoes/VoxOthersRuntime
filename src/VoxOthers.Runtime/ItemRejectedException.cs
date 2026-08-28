namespace VoxOthers.Runtime;

/// <summary>
/// O item, como veio, não pode ser importado — e repetir não vai adiantar.
/// </summary>
/// <remarks>
/// <para>
/// A distinção que este tipo carrega é entre <b>dado ruim</b> e <b>ambiente
/// ruim</b>, e ela existe porque as duas coisas pedem ações opostas de quem for
/// resolver. Mídia que não existe é problema da origem: reprocessar mil vezes
/// dá o mesmo resultado, alguém precisa corrigir o Builder. Banco fora do ar é
/// problema do ambiente: o item está perfeito e vai entrar assim que o banco
/// voltar.
/// </para>
/// <para>
/// Quem trata a falha usa este tipo para decidir: tudo o que <b>não</b> for
/// <see cref="ItemRejectedException"/> é tratado como falha de infraestrutura,
/// que é o lado seguro de errar — um item bom marcado como problema de
/// ambiente só custa um reprocessamento, enquanto um item ruim marcado como
/// falha passageira volta para sempre.
/// </para>
/// </remarks>
public class ItemRejectedException(string message, Exception? inner = null)
    : Exception(message, inner);
