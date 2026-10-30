# Comparação antes vs depois

## Controlo de acesso (R1)
| ID | Teste | antes | depois |
|---|---|---|---|
| T1.1 | Lista de utilizadores sem sessão | EXPOSTO | PROTEGIDO |
| T1.2 | Todos os registos de acesso sem sessão | EXPOSTO | PROTEGIDO |
| T1.3 | Histórico de B sem sessão | EXPOSTO | PROTEGIDO |
| T1.4 | Registos de acesso de B sem sessão | EXPOSTO | PROTEGIDO |
| T1.5 | Todo o histórico sem sessão | EXPOSTO | PROTEGIDO |
| T1.6 | Repositórios recentes sem sessão | PROTEGIDO (redirect) | PROTEGIDO |
| T1.7 | Histórico próprio sem sessão (endpoint novo) | PROTEGIDO | PROTEGIDO |
| T1.8 | Registos gerais de administração sem sessão (endpoint novo) | PROTEGIDO | PROTEGIDO |
| T2.1 | Histórico de A com sessão A | N/A (endpoint inexistente) | OK |
| T2.2 | Registos de acesso de A com sessão A | N/A (endpoint inexistente) | OK |
| T2.3 | Repositórios recentes de A com sessão A | OK | OK |
| T3.1 | Histórico de B pedido pela sessão A | EXPOSTO | PROTEGIDO |
| T3.2 | Registos de B pedidos pela sessão A | EXPOSTO | PROTEGIDO |
| T3.3 | Todos os registos de acesso pela sessão A | EXPOSTO | PROTEGIDO |
| T3.4 | Todo o histórico pela sessão A | EXPOSTO | PROTEGIDO |
| T3.5 | Histórico com username=B injetado pela sessão A | N/A (endpoint inexistente) | OK |
| T3.6 | Recentes com username=B injetado pela sessão A | OK | OK |
| T3.7 | Registos gerais pela sessão A sem permissão de administração | PROTEGIDO | PROTEGIDO |
| T4.1 | Listagem de repositórios por A altera histórico de B | FALHA (500) | PROTEGIDO |

## Conservação (R2)
## Revogação de acesso e cache (R4)
