-- Daily usage per headset, for the allowance in the harley function.
create table if not exists public.harley_usage (
  device text not null,
  day    date not null default current_date,
  kind   text not null,
  count  integer not null default 0,
  primary key (device, day, kind)
);
alter table public.harley_usage enable row level security; -- no public access; only the function (service role)

create or replace function public.harley_count(p_device text, p_kind text)
returns integer
language sql
security definer
set search_path = public
as $$
  insert into harley_usage (device, day, kind, count)
  values (p_device, current_date, p_kind, 1)
  on conflict (device, day, kind) do update set count = harley_usage.count + 1
  returning count;
$$;
revoke all on function public.harley_count(text, text) from public, anon, authenticated;

-- How much is used today (run in the SQL editor):
--   select kind, count(distinct device) as headsets, sum(count) as calls from harley_usage where day = current_date group by kind;
