#!/usr/bin/env bash
# Kit de avaliação das alterações de privacidade (relatório PD, secção 5.3.2).
# Repete sempre os mesmos pedidos para comparar o comportamento antes e depois das alterações.
# Compatível com o bash 3.2 do macOS. Requer: curl, docker (contentores postgres, redis e backend a correr).

set -u

DIR="$(cd "$(dirname "$0")" && pwd)"
CONFIG="$DIR/config.local"

die() { echo "Erro: $*" >&2; exit 1; }
info() { echo "==> $*"; }

carregar_config() {
    [ -f "$CONFIG" ] || die "falta $CONFIG (copia avaliacao/config.example e preenche)"
    # shellcheck source=/dev/null
    . "$CONFIG"
    BASE_URL="${BASE_URL:-http://localhost:5053}"
    local v val
    for v in USER_A USER_B COOKIE_A COOKIE_B REPO_A; do
        eval "val=\${$v:-}"
        [ -n "$val" ] || die "a variável $v não está definida em config.local"
    done
    command -v curl >/dev/null || die "curl não encontrado"
    command -v docker >/dev/null || die "docker não encontrado"
    docker exec postgres true 2>/dev/null || die "o contentor postgres não está a correr (docker compose up)"
    [ "$(http_status "$BASE_URL/login")" != "000" ] || die "o backend não responde em $BASE_URL"
}

preparar_saida() {
    [ -n "${1:-}" ] || die "indica a fase (ex.: antes, depois)"
    FASE="$1"
    OUT="$DIR/resultados/$FASE"
    RAW="$OUT/raw"
    mkdir -p "$RAW"
    {
        echo "fase=$FASE"
        echo "data_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        echo "commit=$(git -C "$DIR/.." rev-parse --short HEAD 2>/dev/null || echo desconhecido)"
        echo "branch=$(git -C "$DIR/.." rev-parse --abbrev-ref HEAD 2>/dev/null || echo desconhecido)"
    } > "$OUT/meta.txt"
}

http_status() { curl -s -o /dev/null -w '%{http_code}' "$1"; }

codificar() { printf '%s' "$1" | sed 's#/#%2F#g'; }

psql_db() { docker exec -i postgres psql -U exampleuser -d gitdashdb -At -F ';' -v ON_ERROR_STOP=1 "$@"; }

contem() { grep -qiF -- "$2" "$1" && echo sim || echo não; }

# Classifica a resposta: "negado" = não deve devolver dados; "proprio" = só pode devolver dados da sessão.
classificar() {
    local esperado="$1" status="$2" tem_b="$3" bytes="$4"
    [ "$status" = "000" ] && { echo "SEM LIGAÇÃO"; return; }
    if [ "$esperado" = "negado" ]; then
        case "$status" in
            401|403|404) echo "PROTEGIDO" ;;
            302) echo "PROTEGIDO (redirect)" ;;
            2??) if [ "$tem_b" = "sim" ] || [ "$bytes" -gt 2 ]; then echo "EXPOSTO"; else echo "PROTEGIDO (vazio)"; fi ;;
            *) echo "VERIFICAR ($status)" ;;
        esac
    else
        case "$status" in
            2??) if [ "$tem_b" = "sim" ]; then echo "FUGA (dados de B)"; else echo "OK"; fi ;;
            404) echo "N/A (endpoint inexistente)" ;;
            *) echo "VERIFICAR ($status)" ;;
        esac
    fi
}

# pedido ID DESCRICAO SESSAO(A|B|-) ESPERADO(negado|proprio) CAMINHO
pedido() {
    local id="$1" desc="$2" sessao="$3" esperado="$4" caminho="$5"
    local cookie="" corpo="$RAW/$id.txt" status bytes tem_a tem_b
    case "$sessao" in A) cookie="$COOKIE_A" ;; B) cookie="$COOKIE_B" ;; esac
    if [ -n "$cookie" ]; then
        status=$(curl -s -o "$corpo" -w '%{http_code}' -H "Cookie: $cookie" "$BASE_URL$caminho")
    else
        status=$(curl -s -o "$corpo" -w '%{http_code}' "$BASE_URL$caminho")
    fi
    bytes=$(wc -c < "$corpo" | tr -d ' ')
    tem_a=$(contem "$corpo" "$USER_A")
    tem_b=$(contem "$corpo" "$USER_B")
    printf '%s;%s;%s;%s;%s;%s;%s;%s;%s;%s\n' "$id" "$desc" "$sessao" "$caminho" "$esperado" \
        "$status" "$bytes" "$tem_a" "$tem_b" "$(classificar "$esperado" "$status" "$tem_b" "$bytes")" >> "$CSV"
    printf '  %-6s %-3s %s\n' "$id" "$status" "$desc"
}

snapshot_historico() {
    psql_db -v login="$1" <<'SQL'
select r.name, r.last_visited, r.visits_number
from repository r join "user" u on u.id = r.user_id
where u.username = :'login'
order by r.name;
SQL
}

precondicoes() {
    psql_db -v a="$USER_A" -v b="$USER_B" <<'SQL'
select
  (select count(*) from repository r join "user" u on u.id = r.user_id where u.username = :'a'),
  (select count(*) from repository r join "user" u on u.id = r.user_id where u.username = :'b'),
  (select count(*) from repository r join "user" u on u.id = r.user_id
    where u.username = :'b' and lower(r.name) like lower(:'b') || '/%'),
  (select count(*) from log l join "user" u on u.id = l.user_id where u.username = :'b');
SQL
}

# ---------------------------------------------------------------- acesso (R1)
cmd_acesso() {
    carregar_config
    preparar_saida "${1:-}"
    CSV="$OUT/acesso.csv"
    echo "id;descricao;sessao;pedido;esperado;status;bytes;contem_A;contem_B;resultado" > "$CSV"

    local pre hist_a hist_b hist_b_proprio logs_b
    pre="$(precondicoes)"
    IFS=';' read -r hist_a hist_b hist_b_proprio logs_b <<EOF
$pre
EOF
    echo "precondicoes: historico_A=$hist_a historico_B=$hist_b historico_B_repos_de_B=$hist_b_proprio logs_B=$logs_b" >> "$OUT/meta.txt"
    [ "${hist_b_proprio:-0}" -gt 0 ] || echo "Aviso: B ainda não abriu nenhum repositório próprio no GitDash; os testes de fuga ficam inconclusivos." >&2
    [ "${logs_b:-0}" -gt 0 ] || echo "Aviso: B ainda não tem registos de acesso; T1.4 e T3.2 ficam inconclusivos." >&2

    info "Acesso sem sessão"
    pedido T1.1 "Lista de utilizadores sem sessão" - negado "/api/Db/user"
    pedido T1.2 "Todos os registos de acesso sem sessão" - negado "/api/Db/log"
    pedido T1.3 "Histórico de B sem sessão" - negado "/api/Db/user/$USER_B/repositories"
    pedido T1.4 "Registos de acesso de B sem sessão" - negado "/api/Db/user/$USER_B/logs"
    pedido T1.5 "Todo o histórico sem sessão" - negado "/api/Db/repository"
    pedido T1.6 "Repositórios recentes sem sessão" - negado "/api/github/recentRepositories"
    pedido T1.7 "Histórico próprio sem sessão (endpoint novo)" - negado "/api/me/history"
    pedido T1.8 "Registos gerais de administração sem sessão (endpoint novo)" - negado "/api/admin/logs"

    info "Sessão A a consultar os seus dados"
    pedido T2.1 "Histórico de A com sessão A" A proprio "/api/me/history"
    pedido T2.2 "Registos de acesso de A com sessão A" A proprio "/api/me/logs"
    pedido T2.3 "Repositórios recentes de A com sessão A" A proprio "/api/github/recentRepositories"

    info "Sessão A a manipular nomes e identificadores"
    pedido T3.1 "Histórico de B pedido pela sessão A" A negado "/api/Db/user/$USER_B/repositories"
    pedido T3.2 "Registos de B pedidos pela sessão A" A negado "/api/Db/user/$USER_B/logs"
    pedido T3.3 "Todos os registos de acesso pela sessão A" A negado "/api/Db/log"
    pedido T3.4 "Todo o histórico pela sessão A" A negado "/api/Db/repository"
    pedido T3.5 "Histórico com username=B injetado pela sessão A" A proprio "/api/me/history?username=$USER_B"
    pedido T3.6 "Recentes com username=B injetado pela sessão A" A proprio "/api/github/recentRepositories?username=$USER_B"
    pedido T3.7 "Registos gerais pela sessão A sem permissão de administração" A negado "/api/admin/logs"

    info "Atualização do histórico: um pedido de A não pode alterar o histórico de B"
    teste_atualizacao

    info "Resultados em $CSV"
}

teste_atualizacao() {
    local antes depois status partilhados=0 nome resultado
    antes="$(snapshot_historico "$USER_B")"
    status=$(curl -s -o "$RAW/T4.1.txt" -w '%{http_code}' -H "Cookie: $COOKIE_A" "$BASE_URL/api/github/repositories")
    sleep 1
    depois="$(snapshot_historico "$USER_B")"
    printf '%s\n' "$antes" > "$RAW/T4.1_historico_B_antes.txt"
    printf '%s\n' "$depois" > "$RAW/T4.1_historico_B_depois.txt"

    while IFS=';' read -r nome _; do
        [ -n "$nome" ] && grep -qF "\"$nome\"" "$RAW/T4.1.txt" && partilhados=$((partilhados + 1))
    done <<EOF
$antes
EOF

    if [ "$antes" != "$depois" ]; then
        resultado="EXPOSTO (histórico de B alterado por A)"
    elif [ "$status" = "500" ]; then
        resultado="FALHA (500)"
    elif [ "$partilhados" -eq 0 ]; then
        resultado="N/A (sem repositório partilhado no histórico de B)"
    else
        resultado="PROTEGIDO"
    fi
    printf '%s;%s;%s;%s;%s;%s;%s;%s;%s;%s\n' T4.1 "Listagem de repositórios por A altera histórico de B" A \
        "/api/github/repositories" "inalterado" "$status" "-" "-" "-" "$resultado" >> "$CSV"
    printf '  %-6s %-3s %s\n' T4.1 "$status" "$resultado"
}

# ---------------------------------------------------------------- conservação (R2)
cmd_retencao() {
    local acao="${1:-}"
    carregar_config
    case "$acao" in
        preparar)
            info "A criar registos com datas controladas para $USER_A"
            local res h l r
            res=$(psql_db -q -v login="$USER_A" -v repo="$REPO_A" < "$DIR/sql/seed_retencao.sql") \
                || die "falha ao criar os registos de teste"
            IFS=';' read -r h l r <<EOF
$res
EOF
            echo "  histórico de teste: $h | registos de teste: $l | $REPO_A no histórico de A: $r"
            [ "${h:-0}" -gt 0 ] || die "a conta $USER_A não existe na base de dados (inicia sessão no GitDash primeiro)"
            [ "${r:-0}" -gt 0 ] || echo "Aviso: $REPO_A não está no histórico de A; abre-o primeiro no GitDash." >&2
            info "A visitar $REPO_A com a sessão A (a entrada deve ficar no histórico)"
            curl -s -o /dev/null -w '  status %{http_code}\n' -H "Cookie: $COOKIE_A" \
                "$BASE_URL/api/github/stats/$(codificar "$REPO_A")/1%20week"
            echo "Corre 'retencao verificar <fase>' na próxima hora (as entradas de 89d23h e 179d23h passam o limite depois disso)."
            ;;
        verificar)
            preparar_saida "${2:-}"
            CSV="$OUT/retencao.csv"
            echo "id;registo;idade;esperado;existe;resultado" > "$CSV"
            verificar_registo H1 historico "pd-teste/historico-030d" "30 dias" manter
            verificar_registo H2 historico "pd-teste/historico-089d23h" "89 dias e 23 h" manter
            verificar_registo H3 historico "pd-teste/historico-090d01h" "90 dias e 1 h" eliminar
            verificar_registo H4 historico "pd-teste/historico-120d" "120 dias" eliminar
            verificar_registo H5 historico "$REPO_A" "120 dias mas revisitado" manter
            verificar_registo L1 log "/pd-teste/log-100d" "100 dias" manter
            verificar_registo L2 log "/pd-teste/log-179d23h" "179 dias e 23 h" manter
            verificar_registo L3 log "/pd-teste/log-180d01h" "180 dias e 1 h" eliminar
            verificar_registo L4 log "/pd-teste/log-200d" "200 dias" eliminar
            info "Resultados em $CSV"
            ;;
        limpar)
            psql_db < "$DIR/sql/limpar_teste.sql" >/dev/null && info "Registos pd-teste removidos"
            ;;
        *) die "uso: retencao preparar | retencao verificar <fase> | retencao limpar" ;;
    esac
}

contar_historico() {
    psql_db -v login="$USER_A" -v chave="$1" <<'SQL'
select count(*) from repository r join "user" u on u.id = r.user_id
where u.username = :'login' and r.name = :'chave';
SQL
}

contar_log() {
    psql_db -v login="$USER_A" -v chave="$1" <<'SQL'
select count(*) from log l join "user" u on u.id = l.user_id
where u.username = :'login' and l.visited_endpoint = :'chave';
SQL
}

verificar_registo() {
    local id="$1" tipo="$2" chave="$3" idade="$4" esperado="$5" n existe resultado
    if [ "$tipo" = "historico" ]; then n=$(contar_historico "$chave"); else n=$(contar_log "$chave"); fi
    if [ "${n:-0}" -gt 0 ]; then existe=sim; else existe=não; fi
    case "$esperado:$existe" in
        manter:sim|eliminar:não) resultado="OK" ;;
        eliminar:sim) resultado="NÃO CONFORME (conservado além do prazo)" ;;
        manter:não) resultado="NÃO CONFORME (eliminado antes do prazo)" ;;
    esac
    printf '%s;%s;%s;%s;%s;%s\n' "$id" "$chave" "$idade" "$esperado" "$existe" "$resultado" >> "$CSV"
    printf '  %-3s %-9s %-28s %s\n' "$id" "$esperado" "$chave" "$resultado"
}

# ---------------------------------------------------------------- cache (R4)
cmd_cache() {
    carregar_config
    preparar_saida "${1:-}"
    local csv="$OUT/cache.csv" repo_nome="${REPO_A#*/}" chave ttl legivel status
    status=$(curl -s -o "$RAW/C1.txt" -w '%{http_code}' -H "Cookie: $COOKIE_A" \
        "$BASE_URL/api/github/stats/$(codificar "$REPO_A")/1%20week")
    echo "pedido_stats_status=$status" >> "$OUT/meta.txt"
    echo "chave;ttl_segundos;nomes_legiveis" > "$csv"
    docker exec redis redis-cli --scan --pattern '*' | tr -d '\r' > "$RAW/chaves.txt"
    while IFS= read -r chave; do
        [ -z "$chave" ] && continue
        ttl=$(docker exec redis redis-cli TTL "$chave" | tr -d '\r')
        legivel=não
        if printf '%s' "$chave" | grep -qiF -- "$repo_nome"; then legivel=sim; fi
        printf '%s;%s;%s\n' "$chave" "$ttl" "$legivel" >> "$csv"
    done < "$RAW/chaves.txt"
    info "Entradas na cache: $(($(wc -l < "$csv") - 1)). Resultados em $csv"
}

cmd_cache_expiracao() {
    carregar_config
    preparar_saida "${1:-}"
    local url ttl chave misses
    url="$BASE_URL/api/github/stats/$(codificar "$REPO_A")/1%20week"
    curl -s -o /dev/null -H "Cookie: $COOKIE_A" "$url"
    chave=$(docker exec redis redis-cli --scan --pattern '*stats*' | tr -d '\r' | head -n 1)
    [ -n "$chave" ] || die "nenhuma entrada de estatísticas encontrada no Redis"
    ttl=$(docker exec redis redis-cli TTL "$chave" | tr -d '\r')
    info "Entrada $chave expira em ${ttl}s. A aguardar..."
    sleep $((ttl + 5))
    local existe; existe=$(docker exec redis redis-cli EXISTS "$chave" | tr -d '\r')
    curl -s -o /dev/null -H "Cookie: $COOKIE_A" "$url"
    sleep 1
    misses=$(docker logs --since 30s backend 2>&1 | grep -c "Cache miss")
    {
        echo "id;chave;ttl_inicial;existe_apos_prazo;novo_pedido_foi_ao_github"
        printf 'E1;%s;%s;%s;%s\n' "$chave" "$ttl" "$([ "$existe" = "0" ] && echo não || echo sim)" \
            "$([ "$misses" -gt 0 ] && echo sim || echo não)"
    } > "$OUT/cache_expiracao.csv"
    info "Resultados em $OUT/cache_expiracao.csv"
}

cmd_revogacao() {
    carregar_config
    preparar_saida "${1:-}"
    [ -n "${REPO_PARTILHADO:-}" ] || die "define REPO_PARTILHADO em config.local"
    local url s1 s2 resultado
    url="$BASE_URL/api/github/stats/$(codificar "$REPO_PARTILHADO")/1%20week"
    CSV="$OUT/revogacao.csv"
    echo "id;descricao;status;resultado" > "$CSV"
    s1=$(curl -s -o "$RAW/V1.txt" -w '%{http_code}' -H "Cookie: $COOKIE_B" "$url")
    echo "V1;B consulta o repositório partilhado com acesso;$s1;$([ "$s1" = "200" ] && echo OK || echo "VERIFICAR ($s1)")" >> "$CSV"
    echo "Pedido inicial de B: $s1"
    echo "Remove agora o acesso de B: GitHub > $REPO_PARTILHADO > Settings > Collaborators > Remove. Depois carrega Enter."
    read -r _
    s2=$(curl -s -o "$RAW/V2.txt" -w '%{http_code}' -H "Cookie: $COOKIE_B" "$url")
    case "$s2" in
        200) resultado="EXPOSTO (resposta reutilizada sem acesso)" ;;
        401|403|404) resultado="PROTEGIDO" ;;
        *) resultado="VERIFICAR ($s2)" ;;
    esac
    echo "V2;B consulta o mesmo repositório depois de perder o acesso;$s2;$resultado" >> "$CSV"
    info "Depois da revogação: $s2 -> $resultado. Resultados em $CSV"
}

# ---------------------------------------------------------------- comparação
comparar_csv() {
    # comparar_csv FICHEIRO_A FICHEIRO_B COLUNA_DESCRICAO COLUNA_RESULTADO ROTULO_A ROTULO_B
    [ -f "$1" ] && [ -f "$2" ] || return 0
    awk -F';' -v cd="$3" -v cr="$4" -v fa="$5" -v fb="$6" '
        FNR == 1 { next }
        NR == FNR { d[$1] = $cd; ra[$1] = $cr; ordem[++n] = $1; next }
        { rb[$1] = $cr; if (!($1 in d)) { d[$1] = $cd; ordem[++n] = $1 } }
        END {
            print "| ID | Teste | " fa " | " fb " |"
            print "|---|---|---|---|"
            for (i = 1; i <= n; i++) {
                id = ordem[i]
                print "| " id " | " d[id] " | " ((id in ra) ? ra[id] : "-") " | " ((id in rb) ? rb[id] : "-") " |"
            }
        }' "$1" "$2"
    echo
}

cmd_comparar() {
    local a="${1:-}" b="${2:-}" ra rb out
    [ -n "$a" ] && [ -n "$b" ] || die "uso: comparar <fase1> <fase2>"
    ra="$DIR/resultados/$a"; rb="$DIR/resultados/$b"
    out="$DIR/resultados/comparacao_${a}_${b}.md"
    {
        echo "# Comparação $a vs $b"
        echo
        echo "## Controlo de acesso (R1)"
        comparar_csv "$ra/acesso.csv" "$rb/acesso.csv" 2 10 "$a" "$b"
        echo "## Conservação (R2)"
        comparar_csv "$ra/retencao.csv" "$rb/retencao.csv" 2 6 "$a" "$b"
        echo "## Revogação de acesso e cache (R4)"
        comparar_csv "$ra/revogacao.csv" "$rb/revogacao.csv" 2 4 "$a" "$b"
    } > "$out"
    info "Comparação em $out"
}

uso() {
    cat <<'EOF'
uso: avaliacao/avaliar.sh <comando>

  acesso <fase>               pedidos de controlo de acesso (R1)
  retencao preparar           cria registos com datas controladas (R2)
  retencao verificar <fase>   confirma o que foi conservado ou eliminado (R2)
  retencao limpar             remove os registos de teste
  cache <fase>                lista as entradas do Redis e o respetivo TTL (R4)
  cache-expiracao <fase>      espera pelo fim do TTL e confirma novo pedido ao GitHub (R4)
  revogacao <fase>            reutilização da cache depois de perder acesso (R4, interativo)
  comparar <fase1> <fase2>    tabela markdown com os resultados lado a lado

Fases sugeridas: antes, depois.
EOF
}

cmd="${1:-}"
[ $# -gt 0 ] && shift
case "$cmd" in
    acesso) cmd_acesso "$@" ;;
    retencao) cmd_retencao "$@" ;;
    cache) cmd_cache "$@" ;;
    cache-expiracao) cmd_cache_expiracao "$@" ;;
    revogacao) cmd_revogacao "$@" ;;
    comparar) cmd_comparar "$@" ;;
    *) uso; [ -z "$cmd" ] || exit 1 ;;
esac
