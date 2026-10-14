-- Registos de teste com datas controladas (relatório, secção 5.3.2).
-- Variáveis psql: login (conta A) e repo (repositório já visitado por A).
-- Os nomes começam por pd-teste/ para serem fáceis de identificar e remover.
-- As colunas guardam UTC sem fuso horário, tal como o backend (DateTime.UtcNow).

delete from repository where name like 'pd-teste/%';
delete from log where visited_endpoint like '/pd-teste/%';

-- Histórico: limite de 90 dias desde a última visita.
insert into repository (user_id, name, last_visited, visits_number, created)
select u.id, v.nome, (now() at time zone 'utc') - v.idade, 1, (now() at time zone 'utc') - v.idade
from "user" u
cross join (values
    ('pd-teste/historico-030d',    make_interval(days => 30)),
    ('pd-teste/historico-089d23h', make_interval(days => 89, hours => 23)),
    ('pd-teste/historico-090d01h', make_interval(days => 90, hours => 1)),
    ('pd-teste/historico-120d',    make_interval(days => 120))
) as v(nome, idade)
where u.username = :'login';

-- Registos de acesso: limite de 180 dias desde a criação.
insert into log (user_id, visited_endpoint, created)
select u.id, v.endpoint, (now() at time zone 'utc') - v.idade
from "user" u
cross join (values
    ('/pd-teste/log-100d',     make_interval(days => 100)),
    ('/pd-teste/log-179d23h',  make_interval(days => 179, hours => 23)),
    ('/pd-teste/log-180d01h',  make_interval(days => 180, hours => 1)),
    ('/pd-teste/log-200d',     make_interval(days => 200))
) as v(endpoint, idade)
where u.username = :'login';

-- Repositório real de A envelhecido para 120 dias; o script visita-o a seguir para testar
-- que uma visita recente mantém a entrada no histórico.
update repository
set last_visited = (now() at time zone 'utc') - interval '120 days'
where name = :'repo'
  and user_id = (select id from "user" where username = :'login');

select
    (select count(*) from repository where name like 'pd-teste/%') as historico_teste,
    (select count(*) from log where visited_endpoint like '/pd-teste/%') as logs_teste,
    (select count(*) from repository r join "user" u on u.id = r.user_id
      where u.username = :'login' and r.name = :'repo') as repo_a_presente;
