using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VoxOthers.Runtime.Data;

namespace VoxOthers.Runtime.Registration;

/// <summary>
/// O que o item precisa para virar bilhete: quem é o operador, em que canal ele
/// grava e qual o vínculo entre os dois.
/// </summary>
public sealed record RegistrationResult
{
    /// <summary>Código do usuário na tabela USUARIO. É texto, não número.</summary>
    public required string CodUsuario { get; init; }

    /// <summary>Nome do operador, como sai no campo <c>US</c> do bilhete.</summary>
    public required string UserName { get; init; }

    /// <summary>Canal de gravação. Sai no campo <c>CH</c>.</summary>
    public required int ChannelNumber { get; init; }

    /// <summary>Código do ramal na tabela RAMAL.</summary>
    public required int CodRamal { get; init; }

    /// <summary>Código do vínculo na tabela LOGIN. Sai no campo <c>CL</c>.</summary>
    public required long CodLogin { get; init; }

    /// <summary>Verdadeiro quando o usuário não existia e foi criado agora.</summary>
    public bool UserCreated { get; init; }

    /// <summary>Verdadeiro quando o canal não existia e foi criado agora.</summary>
    public bool ChannelCreated { get; init; }
}

/// <summary>
/// Garante que o operador do item tem usuário, ramal e login na base do Vox.
/// </summary>
/// <remarks>
/// <para>
/// Substitui a dupla <c>UserResolution</c> + <c>ChannelAllocation</c>, que era
/// uma separação errada: não dá para decidir o canal sem saber quem é o
/// usuário, porque um operador que já tem ramal ativo naquela operação
/// <b>reaproveita</b> o canal dele em vez de ganhar um novo. Separados, os dois
/// serviços criavam um ramal por gravação e a tabela RAMAL cresceria uma linha
/// por atendimento.
/// </para>
/// <para>
/// Espelha o comportamento do sistema atual
/// (<c>RegisterUsuarioLogin.RegisterNewUserRamal</c>), inclusive na forma de
/// distribuir códigos, porque os dois sistemas vão conviver durante a migração
/// e um mecanismo diferente de numeração colidiria com o antigo.
/// </para>
/// </remarks>
public interface IVoxRegistration
{
    /// <summary>
    /// Devolve o cadastro do operador naquela operação, criando o que faltar.
    /// </summary>
    Task<RegistrationResult> EnsureAsync(
        int serverId,
        int operationId,
        string? login,
        string? name,
        CancellationToken cancellationToken);
}

public sealed class VoxRegistration : IVoxRegistration
{
    /// <summary>
    /// Quantas vezes tentar quando duas transações disputam o mesmo código.
    /// </summary>
    private const int Tentativas = 5;

    /// <summary>
    /// Quantos códigos ocupados aceitar pular antes de desistir.
    /// </summary>
    private const int CodigosParaPular = 500;

    /// <summary>
    /// O Firebird abre transação Serializable (consistency) e trava as tabelas
    /// que a transação toca (SEQUENCE_ID, USUARIO, RAMAL, LOGIN). Com os 4
    /// workers do pipeline cadastrando operador novo ao mesmo tempo, as
    /// transações disputam o lock das mesmas tabelas e só uma vence — o retry
    /// ajuda, mas com vários disputando o perdedor raramente completa a tempo.
    /// Cadastro é rápido, então o mais simples é um por vez: o semáforo
    /// serializa os cadastros DESTE processo. Disputa com outros processos
    /// (o Vox legado na mesma base) continua coberta pelo retry.
    /// </summary>
    private static readonly SemaphoreSlim _cadastroLock = new(1, 1);

    private readonly VoxDbContext _db;
    private readonly ILogger<VoxRegistration> _logger;

    public VoxRegistration(VoxDbContext db, ILogger<VoxRegistration> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RegistrationResult> EnsureAsync(
        int serverId,
        int operationId,
        string? login,
        string? name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) && string.IsNullOrWhiteSpace(name))
        {
            throw new ItemRejectedException(
                "Item sem login e sem nome de operador: não há como identificar quem atendeu.");
        }

        // COD_SERVIDOR é SMALLINT na base. Deixar passar daria erro de banco no
        // meio da transação, que seria tratado como falha de ambiente e
        // reprocessado para sempre — quando na verdade o dado é que está errado.
        if (serverId is <= 0 or > short.MaxValue)
        {
            throw new ItemRejectedException(
                $"ServerId {serverId} fora da faixa aceita pelo Vox (1 a {short.MaxValue}).");
        }

        // Um cadastro por vez no processo: ver o comentário do _cadastroLock.
        await _cadastroLock.WaitAsync(cancellationToken);
        try
        {
            return await ExecutarComSerializacaoAsync(
                serverId, operationId, login, name, cancellationToken);
        }
        finally
        {
            _cadastroLock.Release();
        }
    }

    private async Task<RegistrationResult> ExecutarComSerializacaoAsync(
        int serverId,
        int operationId,
        string? login,
        string? name,
        CancellationToken cancellationToken)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                return await ExecutarAsync(serverId, operationId, login, name, cancellationToken);
            }
            catch (Exception ex) when (EhReferenciaInexistente(ex))
            {
                // Operação ou servidor que não existem no Vox. É problema do
                // dado, não do ambiente: repetir daria o mesmo erro para
                // sempre, e o item ficaria em laço eterno na reentrega.
                throw new ItemRejectedException(
                    $"Operação {operationId} ou servidor {serverId} não existem no Vox. " +
                    $"O banco recusou: {MensagemDoBanco(ex)}", ex);
            }
            catch (Exception ex) when (EhConflitoDeConcorrencia(ex) && tentativa < Tentativas)
            {
                // Outra transação pegou o mesmo código primeiro. Repetir é o
                // certo: na segunda passada ou o cadastro do outro já está
                // visível e é reaproveitado, ou o MAX+1 devolve outro número.
                _logger.Here().Warn(
                    "Conflito ao cadastrar operador (tentativa {Tentativa}/{Total}): {Mensagem}",
                    tentativa, Tentativas, ex.Message);

                _db.ChangeTracker.Clear();

                // Sob Serializable o Firebird resolve a disputa da SEQUENCE_ID
                // com "lock conflict" (no-wait): o perdedor precisa esperar o
                // vencedor commitar antes de tentar de novo, senão retrya em
                // loop sem nunca vencer. Um atraso curto e aleatório quebra o
                // lockstep dos 4 workers.
                await Task.Delay(Random.Shared.Next(50, 301), cancellationToken);
            }
        }
    }

    private async Task<RegistrationResult> ExecutarAsync(
        int serverId,
        int operationId,
        string? login,
        string? name,
        CancellationToken cancellationToken)
    {
        // Serializable, e não ReadCommitted, porque a leitura crítica é um
        // MAX(): sob ReadCommitted duas transações leem o mesmo máximo e
        // inserem o mesmo canal. Serializable faz o banco travar a faixa lida,
        // então a segunda espera ou falha — e falhar é tratado acima. O sistema
        // atual lê o MAX *fora* de qualquer transação, que é justamente o
        // defeito que não se repete aqui.
        await using var transacao = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var usuario = await GarantirUsuarioAsync(login, name, cancellationToken);

        // Operador que já grava nesta operação e neste servidor mantém o canal
        // dele. É o que impede a tabela RAMAL de crescer a cada atendimento.
        var existente = await _db.Logins
            .Join(_db.Ramais, l => l.CodRamal, r => r.CodRamal, (l, r) => new { l, r })
            .Where(x => x.l.CodUsuario == usuario.CodUsuario
                     && x.r.CodOperacao == operationId
                     && x.r.CodServidor == serverId
                     && x.r.FlgRegAtivo == "S")
            .Select(x => new { x.r.NumCanal, x.r.CodRamal, x.l.CodLogin })
            .FirstOrDefaultAsync(cancellationToken);

        if (existente != null)
        {
            await transacao.CommitAsync(cancellationToken);

            _logger.Here().Debug(
                "Operador {Usuario} já cadastrado na operação {Operacao}: canal {Canal}",
                usuario.CodUsuario, operationId, existente.NumCanal);

            return new RegistrationResult
            {
                CodUsuario = usuario.CodUsuario,
                UserName = usuario.NomUsuario ?? string.Empty,
                ChannelNumber = existente.NumCanal,
                CodRamal = existente.CodRamal,
                CodLogin = existente.CodLogin,
                UserCreated = usuario.Criado
            };
        }

        var ramal = await CriarRamalAsync(serverId, operationId, cancellationToken);

        // O ramal é gravado ANTES do login, e não junto. LOGIN tem chave
        // estrangeira para RAMAL, mas o modelo não declara essa relação — as
        // duas entidades são independentes para o EF, que então escolhe a ordem
        // dos INSERT por conta própria e pode mandar o LOGIN primeiro. O banco
        // recusa. Isto não aparecia em teste porque o banco da suíte é SQLite,
        // criado a partir do mesmo modelo: sem a relação declarada, ele nasce
        // sem a chave estrangeira e aceita qualquer ordem. Foi a gravação na
        // base real que mostrou.
        await _db.SaveChangesAsync(cancellationToken);

        var codLogin = await CriarLoginAsync(ramal.CodRamal, usuario.CodUsuario, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        _logger.Here().Info(
            "Operador {Usuario} cadastrado na operação {Operacao} do servidor {Servidor}: " +
            "canal {Canal}, ramal {Ramal}, login {Login}",
            usuario.CodUsuario, operationId, serverId, ramal.NumCanal, ramal.CodRamal, codLogin);

        return new RegistrationResult
        {
            CodUsuario = usuario.CodUsuario,
            UserName = usuario.NomUsuario ?? string.Empty,
            ChannelNumber = ramal.NumCanal,
            CodRamal = ramal.CodRamal,
            CodLogin = codLogin,
            UserCreated = usuario.Criado,
            ChannelCreated = true
        };
    }

    /// <summary>
    /// Encontra o usuário pelo login, ou o cria.
    /// </summary>
    /// <remarks>
    /// A busca é só pelo login, sem servidor, porque a tabela USUARIO não tem
    /// servidor — o mesmo operador pode gravar em mais de um. Isso é do
    /// desenho do Vox, não um esquecimento.
    /// </remarks>
    private async Task<(string CodUsuario, string? NomUsuario, bool Criado)> GarantirUsuarioAsync(
        string? login,
        string? name,
        CancellationToken cancellationToken)
    {
        var loginNormalizado = login?.Trim().ToUpperInvariant();

        if (!string.IsNullOrEmpty(loginNormalizado))
        {
            var achado = await _db.Usuarios
                .Where(u => u.Login == loginNormalizado)
                .Select(u => new { u.CodUsuario, u.NomUsuario })
                .FirstOrDefaultAsync(cancellationToken);

            // Trim porque COD_USUARIO é CHAR(20): o Firebird devolve o valor
            // completado com espaços, e esse texto ainda viaja para o bilhete.
            if (achado != null) return (achado.CodUsuario.Trim(), achado.NomUsuario?.Trim(), false);
        }

        // Sem login utilizável, o nome é o que resta para não criar um usuário
        // novo a cada gravação do mesmo operador.
        if (string.IsNullOrEmpty(loginNormalizado))
        {
            var porNome = await _db.Usuarios
                .Where(u => u.NomUsuario == name)
                .Select(u => new { u.CodUsuario, u.NomUsuario })
                .FirstOrDefaultAsync(cancellationToken);

            if (porNome != null) return (porNome.CodUsuario.Trim(), porNome.NomUsuario?.Trim(), false);
        }

        var codigo = await ProximoCodigoDeUsuarioAsync(cancellationToken);

        // Os limites saem do tamanho real das colunas, e não de números
        // arredondados: NOM_USUARIO tem 70 e não 100. Cortar em 100 daria erro
        // de banco em qualquer operador com nome longo.
        var nomeCompleto = Cortar(
            string.IsNullOrWhiteSpace(name) ? loginNormalizado : name, VoxDbContext.NomeMaximo)!;

        var loginGravado = Cortar(loginNormalizado ?? nomeCompleto, VoxDbContext.LoginMaximo)!;

        var novo = new VoxUsuario
        {
            CodUsuario = codigo,
            NomUsuario = nomeCompleto,
            CodCargo = 2,
            Login = loginGravado,
            FlgAtivo = "S",
            // NOM_CURTO comporta 30, mas o sistema atual grava 8. Mantido em 8
            // para que um usuário criado aqui seja indistinguível de um criado lá.
            NomCurto = Cortar(loginGravado, 8),
            CompanyId = 1,
            GuidResetPassword = Guid.NewGuid().ToString(),
            IsSystemActive = "S",
            RegistrationDate = DateTime.Now,
            CompleteUser = "N"
        };

        _db.Usuarios.Add(novo);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.Here().Info("Usuário criado: {Nome} (código {Codigo})", nomeCompleto, codigo);

        return (codigo, nomeCompleto, true);
    }

    private async Task<VoxRamal> CriarRamalAsync(
        int serverId,
        int operationId,
        CancellationToken cancellationToken)
    {
        // Canal é único por servidor, e o próximo é sempre um a mais que o
        // maior em uso — mesma regra do sistema atual, para que os dois possam
        // conviver sem se atropelar.
        var maiorCanal = await _db.Ramais
            .Where(r => r.CodServidor == serverId && r.FlgRegAtivo == "S")
            .Select(r => (int?)r.NumCanal)
            .MaxAsync(cancellationToken) ?? 0;

        var canal = maiorCanal + 1;

        var maiorRamal = await _db.Ramais
            .Select(r => (int?)r.CodRamal)
            .MaxAsync(cancellationToken) ?? 0;

        var ramal = new VoxRamal
        {
            CodRamal = maiorRamal + 1,
            CodServidor = (short)serverId,
            NumCanal = canal,
            TxtNumRamal = canal.ToString(),
            FlgRamalAtivo = "S",
            FlgRegAtivo = "S",
            CodOperacao = operationId,

            // O sistema atual passa S/S/S nas chamadas do Others. Mantido para
            // que um ramal criado aqui seja indistinguível de um criado lá.
            FlgRamalFixo = "S",
            FlgOperacaoFixa = "S",
            FlgUsuarioFixo = "S"
        };

        _db.Ramais.Add(ramal);
        return ramal;
    }

    private async Task<long> CriarLoginAsync(
        int codRamal,
        string codUsuario,
        CancellationToken cancellationToken)
    {
        var maiorLogin = await _db.Logins
            .Select(l => (long?)l.CodLogin)
            .MaxAsync(cancellationToken) ?? 0;

        var codLogin = maiorLogin + 1;

        _db.Logins.Add(new VoxLogin
        {
            CodLogin = codLogin,
            CodRamal = codRamal,
            CodUsuario = codUsuario,
            DatLogin = DateTime.Now,
            FlgLogoutNorm = "N"
        });

        return codLogin;
    }

    /// <summary>
    /// Devolve um código de usuário livre, avançando a sequência enquanto ela
    /// apontar para código já ocupado.
    /// </summary>
    /// <remarks>
    /// A SEQUENCE_ID não é a única fonte de <c>COD_USUARIO</c> na prática. Na
    /// base de homologação a sequência estava em 9731, o usuário de código
    /// <c>9731</c> já existia, e havia 73 códigos numéricos acima do valor da
    /// sequência — alguns no formato de data e hora, sinal de que outro
    /// componente cria usuário sem consumir a sequência. Confiar no valor puro
    /// faria todo cadastro novo quebrar por chave duplicada até alguém acertar a
    /// sequência à mão.
    /// </remarks>
    private async Task<string> ProximoCodigoDeUsuarioAsync(CancellationToken cancellationToken)
    {
        for (var pulos = 0; pulos < CodigosParaPular; pulos++)
        {
            var numero = await ProximoCodigoAsync("USUARIO", "COD_USUARIO", cancellationToken);
            var codigo = numero.ToString(CultureInfo.InvariantCulture);

            if (!await _db.Usuarios.AnyAsync(u => u.CodUsuario == codigo, cancellationToken))
            {
                return codigo;
            }

            _logger.Here().Warn(
                "Código de usuário {Codigo} veio da SEQUENCE_ID mas já está em uso. Avançando.",
                codigo);
        }

        throw new InvalidOperationException(
            $"A SEQUENCE_ID de USUARIO apontou para {CodigosParaPular} códigos seguidos já ocupados. " +
            "A sequência está muito atrás dos códigos em uso e precisa ser acertada.");
    }

    /// <summary>
    /// Consome um código da SEQUENCE_ID: lê o valor atual, devolve-o e grava o
    /// seguinte.
    /// </summary>
    private async Task<long> ProximoCodigoAsync(
        string tabela,
        string chave,
        CancellationToken cancellationToken)
    {
        var sequencia = await _db.Sequences
            .FirstOrDefaultAsync(s => s.TxtTable == tabela && s.TxtKey == chave, cancellationToken)
            ?? throw new InvalidOperationException(
                $"SEQUENCE_ID não tem linha para a tabela '{tabela}', chave '{chave}'. " +
                "Sem ela não há como gerar código novo sem colidir com o sistema atual.");

        var codigo = sequencia.NumNextValue;

        if (codigo <= 0)
        {
            throw new InvalidOperationException(
                $"SEQUENCE_ID devolveu {codigo} para '{tabela}'.'{chave}'.");
        }

        sequencia.NumNextValue = codigo + 1;
        return codigo;
    }

    private static string? Cortar(string? valor, int limite) =>
        valor is null || valor.Length <= limite ? valor : valor[..limite];

    /// <summary>
    /// O item aponta para operação ou servidor que não existem no Vox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precisa ser reconhecida antes do conflito de concorrência e separada
    /// dele, porque as duas se parecem — as duas são "violação de constraint" —
    /// e pedem o oposto: conflito se resolve repetindo, referência inexistente
    /// nunca se resolve.
    /// </para>
    /// <para>
    /// Só conta a chave estrangeira <b>de RAMAL</b>, que é a única cujo alvo vem
    /// do item: COD_OPERACAO e COD_SERVIDOR. As de LOGIN apontam para linhas que
    /// este mesmo cadastro acabou de criar — se uma delas falhar, o problema é
    /// no código, e chamar isso de "dado ruim" mandaria um item bom para a
    /// quarentena e esconderia o defeito.
    /// </para>
    /// </remarks>
    private static bool EhReferenciaInexistente(Exception ex)
    {
        for (var atual = ex; atual != null; atual = atual.InnerException)
        {
            var mensagem = atual.Message;

            if (mensagem.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
                && mensagem.Contains("table \"RAMAL\"", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A mensagem que o banco realmente deu, e não a do embrulho do EF.
    /// </summary>
    /// <remarks>
    /// O EF entrega "An error occurred while saving the entity changes", que não
    /// ajuda ninguém. Quem for ler isso na quarentena precisa do texto do banco,
    /// que nomeia a constraint.
    /// </remarks>
    private static string MensagemDoBanco(Exception ex)
    {
        var mensagem = ex.Message;

        for (var atual = ex; atual != null; atual = atual.InnerException)
        {
            if (atual.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            {
                mensagem = atual.Message;
            }
        }

        return mensagem;
    }

    /// <summary>
    /// Reconhece o que vale a pena repetir: violação de chave e falha de
    /// serialização (inclui deadlock e o "lock conflict" no-wait que o Firebird
    /// usa quando duas transações disputam a mesma linha, como a SEQUENCE_ID).
    /// </summary>
    private static bool EhConflitoDeConcorrencia(Exception ex)
    {
        for (var atual = ex; atual != null; atual = atual.InnerException)
        {
            var mensagem = atual.Message;

            if (mensagem.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
                || mensagem.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || mensagem.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || mensagem.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)
                || mensagem.Contains("serializ", StringComparison.OrdinalIgnoreCase)
                || mensagem.Contains("lock conflict", StringComparison.OrdinalIgnoreCase)
                || mensagem.Contains("update conflict", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
