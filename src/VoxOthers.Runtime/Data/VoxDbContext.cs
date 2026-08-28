using Microsoft.EntityFrameworkCore;

namespace VoxOthers.Runtime.Data;

/// <summary>
/// Contexto para as tabelas do Vox envolvidas no cadastro: USUARIO, RAMAL,
/// LOGIN e SEQUENCE_ID.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só tabelas que já existem no Vox.</b> Nenhuma entidade daqui é criada por
/// nós: a base é compartilhada com o sistema atual durante a migração, e o
/// Runtime não acrescenta estrutura nela. A deduplicação, que era a única
/// exceção, saiu para disco (AD-5).
/// </para>
/// <para>
/// Este arquivo é o único lugar do projeto que conhece o schema do Vox. Os
/// nomes de coluna foram extraídos do SQL do sistema atual
/// (<c>RegisterUsuarioLogin.cs</c>), que é a referência de como o cadastro
/// funciona hoje.
/// </para>
/// <para>
/// Os <i>tipos</i> foram <b>conferidos contra a base de homologação</b>
/// (Firebird 4.0), lendo o dicionário de dados, e não mais inferidos. A
/// conferência derrubou três suposições que pareciam óbvias: <c>COD_USUARIO</c>
/// é <c>CHAR(20)</c> e não número, <c>COD_OPERACAO</c> é <c>BIGINT</c> e
/// <c>COD_SERVIDOR</c> é <c>SMALLINT</c>. Onde o sistema atual monta SQL por
/// concatenação, um número entre aspas e um texto entre aspas são
/// indistinguíveis — foi por isso que a inferência errou.
/// </para>
/// <para>
/// <b>CHAR e não VARCHAR</b> em <c>COD_USUARIO</c> e <c>TXT_NUM_RAMAL</c>: o
/// Firebird completa o valor com espaços até o tamanho da coluna. Quem lê essas
/// colunas recebe o valor preenchido e precisa aparar antes de usar em texto —
/// dentro do banco a comparação ignora os espaços, fora dele não.
/// </para>
/// </remarks>
public class VoxDbContext : DbContext
{
    public VoxDbContext(DbContextOptions<VoxDbContext> options) : base(options) { }

    /// <summary>Ramais (canais de gravação).</summary>
    public DbSet<VoxRamal> Ramais { get; set; } = null!;

    /// <summary>Vínculo entre usuário e ramal.</summary>
    public DbSet<VoxLogin> Logins { get; set; } = null!;

    /// <summary>Usuários.</summary>
    public DbSet<VoxUsuario> Usuarios { get; set; } = null!;

    /// <summary>Distribuidor de códigos sequenciais do Vox.</summary>
    public DbSet<VoxSequenceId> Sequences { get; set; } = null!;

    /// <summary>Tamanho de <c>USUARIO.NOM_USUARIO</c> na base.</summary>
    public const int NomeMaximo = 70;

    /// <summary>Tamanho de <c>USUARIO.LOGIN</c> na base.</summary>
    public const int LoginMaximo = 202;

    /// <summary>Tamanho de <c>USUARIO.NOM_CURTO</c> na base.</summary>
    public const int NomeCurtoMaximo = 30;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<VoxUsuario>(e =>
        {
            e.ToTable("USUARIO");
            e.HasKey(u => u.CodUsuario);

            // Os códigos vêm da SEQUENCE_ID e do MAX+1, nunca do banco. Sem
            // isto o EF trataria a chave como identity e não a enviaria no
            // INSERT.
            e.Property(u => u.CodUsuario)
                .HasColumnName("COD_USUARIO").HasMaxLength(20).IsFixedLength().ValueGeneratedNever();

            e.Property(u => u.NomUsuario).HasColumnName("NOM_USUARIO").HasMaxLength(NomeMaximo);
            e.Property(u => u.CodCargo).HasColumnName("COD_CARGO");
            e.Property(u => u.Login).HasColumnName("LOGIN").HasMaxLength(LoginMaximo);
            e.Property(u => u.FlgAtivo).HasColumnName("FLG_ATIVO").HasMaxLength(1).IsFixedLength();
            e.Property(u => u.NomCurto).HasColumnName("NOM_CURTO").HasMaxLength(NomeCurtoMaximo);
            e.Property(u => u.CompanyId).HasColumnName("COMPANY_ID");
            e.Property(u => u.GuidResetPassword).HasColumnName("GUID_RESET_PASSWORD");
            e.Property(u => u.IsSystemActive).HasColumnName("IS_SYSTEM_ACTIVE");
            e.Property(u => u.RegistrationDate).HasColumnName("REGISTRATION_DATE");
            e.Property(u => u.CompleteUser).HasColumnName("COMPLETE_USER").HasMaxLength(1).IsFixedLength();
        });

        modelBuilder.Entity<VoxRamal>(e =>
        {
            e.ToTable("RAMAL");
            e.HasKey(r => r.CodRamal);

            e.Property(r => r.CodRamal).HasColumnName("COD_RAMAL").ValueGeneratedNever();
            e.Property(r => r.CodServidor).HasColumnName("COD_SERVIDOR");
            e.Property(r => r.NumCanal).HasColumnName("NUM_CANAL");
            e.Property(r => r.TxtNumRamal).HasColumnName("TXT_NUM_RAMAL").HasMaxLength(40).IsFixedLength();
            e.Property(r => r.FlgRamalAtivo).HasColumnName("FLG_RAMAL_ATIVO");
            e.Property(r => r.FlgRegAtivo).HasColumnName("FLG_REG_ATIVO");
            e.Property(r => r.CodOperacao).HasColumnName("COD_OPERACAO");
            e.Property(r => r.FlgRamalFixo).HasColumnName("FLG_RAMALFIXO");
            e.Property(r => r.FlgOperacaoFixa).HasColumnName("FLG_OPERACAOFIXA");
            e.Property(r => r.FlgUsuarioFixo).HasColumnName("FLG_USUARIOFIXO");
        });

        modelBuilder.Entity<VoxLogin>(e =>
        {
            e.ToTable("LOGIN");
            e.HasKey(l => l.CodLogin);

            e.Property(l => l.CodLogin).HasColumnName("COD_LOGIN").ValueGeneratedNever();
            e.Property(l => l.CodRamal).HasColumnName("COD_RAMAL");
            e.Property(l => l.CodUsuario).HasColumnName("COD_USUARIO").HasMaxLength(20).IsFixedLength();
            e.Property(l => l.DatLogin).HasColumnName("DAT_LOGIN");
            e.Property(l => l.DatLogout).HasColumnName("DAT_LOGOUT");
            e.Property(l => l.FlgLogoutNorm).HasColumnName("FLG_LOGOUT_NORM");
        });

        modelBuilder.Entity<VoxSequenceId>(e =>
        {
            e.ToTable("SEQUENCE_ID");

            // A chave real da tabela é CODIGO, mas quem procura uma sequência
            // procura por tabela e chave. As duas coisas estão aqui: CODIGO
            // como chave, porque é o que o banco exige no INSERT, e o par
            // TXT_TABLE/TXT_KEY é o que a consulta usa.
            e.HasKey(s => s.Codigo);

            e.Property(s => s.Codigo).HasColumnName("CODIGO").ValueGeneratedNever();
            e.Property(s => s.TxtTable).HasColumnName("TXT_TABLE");
            e.Property(s => s.TxtKey).HasColumnName("TXT_KEY");
            e.Property(s => s.NumNextValue).HasColumnName("NUM_NEXT_VALUE");
        });
    }
}

/// <summary>Tabela USUARIO. Note que ela não tem servidor: um usuário é global,
/// e o vínculo com o servidor vem pelo RAMAL.</summary>
public class VoxUsuario
{
    /// <summary>
    /// Código do usuário. <c>CHAR(20)</c> — texto, não número.
    /// </summary>
    /// <remarks>
    /// Parece número porque quase sempre é: 9.526 usuários na base de
    /// homologação, dos quais 127 têm código como <c>MASTER</c> ou
    /// <c>DIEGO</c>. Mapear como inteiro faria o serviço quebrar ao ler
    /// qualquer um desses — e eles são usuários reais, que gravam.
    /// </remarks>
    public string CodUsuario { get; set; } = string.Empty;

    public string? NomUsuario { get; set; }

    /// <summary><c>SMALLINT</c>, com chave estrangeira para CARGO.</summary>
    public short CodCargo { get; set; }
    public string? Login { get; set; }
    public string? FlgAtivo { get; set; }
    public string? NomCurto { get; set; }
    public int CompanyId { get; set; }
    public string? GuidResetPassword { get; set; }
    public string? IsSystemActive { get; set; }
    public DateTime? RegistrationDate { get; set; }

    /// <summary><c>COMPLETE_USER</c>, <c>CHAR(1)</c>. Fixo <c>"N"</c> na criação.</summary>
    public string? CompleteUser { get; set; }
}

/// <summary>Tabela RAMAL. Um ramal pertence a um servidor e a uma operação.</summary>
public class VoxRamal
{
    public int CodRamal { get; set; }

    /// <summary><c>SMALLINT</c>: servidor acima de 32.767 não cabe na coluna.</summary>
    public short CodServidor { get; set; }

    public int NumCanal { get; set; }
    public string? TxtNumRamal { get; set; }
    public string? FlgRamalAtivo { get; set; }
    public string? FlgRegAtivo { get; set; }

    /// <summary><c>BIGINT</c>, com chave estrangeira para OPERACAO.</summary>
    public long CodOperacao { get; set; }
    public string? FlgRamalFixo { get; set; }
    public string? FlgOperacaoFixa { get; set; }
    public string? FlgUsuarioFixo { get; set; }
}

/// <summary>Tabela LOGIN. É o que liga um usuário a um ramal, e o
/// <c>COD_LOGIN</c> é o que sai no campo <c>CL</c> do bilhete.</summary>
public class VoxLogin
{
    public long CodLogin { get; set; }
    public int CodRamal { get; set; }

    /// <summary><c>CHAR(20)</c>, com chave estrangeira para USUARIO.</summary>
    public string CodUsuario { get; set; } = string.Empty;
    public DateTime DatLogin { get; set; }
    public DateTime? DatLogout { get; set; }
    public string? FlgLogoutNorm { get; set; }
}

/// <summary>
/// Tabela SEQUENCE_ID — o distribuidor de códigos do Vox.
/// </summary>
/// <remarks>
/// <c>NUM_NEXT_VALUE</c> guarda o <b>próximo</b> código a usar: quem consome lê
/// o valor, usa esse número e grava valor+1.
/// </remarks>
public class VoxSequenceId
{
    /// <summary>Chave primária da tabela.</summary>
    public int Codigo { get; set; }

    public string TxtTable { get; set; } = string.Empty;
    public string TxtKey { get; set; } = string.Empty;

    /// <summary><c>BIGINT</c>.</summary>
    public long NumNextValue { get; set; }
}
