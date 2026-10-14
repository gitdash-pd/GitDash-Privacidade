# Avaliação das alterações (relatório, secção 5.3.2)

Este kit repete sempre os mesmos pedidos e guarda os resultados em `resultados/<fase>/`.
Corre-se uma vez na tag `baseline`, antes de qualquer alteração (`antes`), e outra vez depois das alterações (`depois`).
Para comparar as duas fases, usa `./avaliacao/avaliar.sh comparar antes depois`.

## Preparação (uma vez)

1. Duas contas GitHub de teste, **A** e **B**, com logins que não estejam contidos um no outro.
2. Com a app a correr (`docker compose up --build`), inicia sessão no GitDash com A numa janela normal e com B numa janela anónima.
3. Dados fictícios:
   - A abre um repositório **seu** no GitDash (fica no histórico de A). É o `REPO_A`.
   - B abre um repositório **seu** no GitDash (assim os dados de B contêm o login de B e qualquer fuga é detetada).
   - A cria um repositório **privado** e convida B como colaborador. B aceita o convite e abre esse repositório no GitDash. É o `REPO_PARTILHADO`, usado no teste de atualização (T4.1) e no de revogação.
4. `cp avaliacao/config.example avaliacao/config.local` e preenche os logins, os repositórios e os cookies.
   Para obter o cookie, abre no Chrome DevTools > Network, clica num pedido para `localhost:5053/api/...` e, em Request Headers, copia o valor de `Cookie`.

Os cookies são credenciais. O `config.local` e as respostas em bruto (`resultados/*/raw/`) estão no `.gitignore`.

## Procedimento

```bash
# fase "antes" (git checkout baseline)
./avaliacao/avaliar.sh acesso antes
./avaliacao/avaliar.sh retencao preparar
./avaliacao/avaliar.sh retencao verificar antes      # até 1 hora depois de preparar
./avaliacao/avaliar.sh cache antes
./avaliacao/avaliar.sh revogacao antes               # interativo: pede para remover B do repo partilhado
./avaliacao/avaliar.sh cache-expiracao antes         # espera pelo fim do TTL (30 min na baseline)

# fase "depois" (com as alterações integradas)
./avaliacao/avaliar.sh acesso depois
./avaliacao/avaliar.sh retencao preparar
docker compose restart backend                        # se a tarefa de eliminação corre no arranque
./avaliacao/avaliar.sh retencao verificar depois
...
./avaliacao/avaliar.sh comparar antes depois
```

No teste de revogação, volta a adicionar B ao repositório antes de repetir o teste noutra fase.

## O que cada teste verifica

| Grupo | Ideia do 5.3.2 | Resultado esperado depois |
|---|---|---|
| T1 | Pedido sem sessão obtém informação protegida? | `PROTEGIDO` (401/404) |
| T2 | A consulta o seu histórico e os seus registos | `OK`, sem dados de B |
| T3 | A altera nomes e identificadores no pedido | `PROTEGIDO` ou `OK` sem dados de B |
| T4 | Um pedido de A altera o histórico de B? | `PROTEGIDO` |
| H/L | Registos antes, perto e depois dos 90/180 dias | Só os que passaram o prazo são eliminados; o repositório revisitado fica |
| cache | Chaves e TTL no Redis | TTL ≤ prazo configurado; chaves sem nomes legíveis |
| revogação | Resposta da cache depois de B perder o acesso | `PROTEGIDO` |
| expiração | A entrada desaparece no fim do TTL e o pedido seguinte vai ao GitHub | `existe_apos_prazo=não`, `novo_pedido_foi_ao_github=sim` |

`EXPOSTO`, `FUGA` e `NÃO CONFORME` na fase `antes` são a evidência do risco. Na fase `depois` indicam uma limitação que tem de ser documentada.
