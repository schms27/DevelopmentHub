import { useMemo, useState } from "react";
import { useQuery, useMutation } from "@tanstack/react-query";
import {
  contributorsApi,
  type ContributorStat,
  type RepositoryRef,
} from "../../api/contributors";
import { ContributorsTabs } from "./ContributorsTabs";
import "./ContributorsPage.css";

type Provider = "azureDevOps" | "github";
type SortKey = "authored" | "reviewed" | "name";

const PROVIDERS: { id: Provider; label: string; disabled?: boolean }[] = [
  { id: "azureDevOps", label: "Azure DevOps" },
  { id: "github", label: "GitHub", disabled: true },
];

export default function ContributorsPage() {
  const [provider, setProvider] = useState<Provider>("azureDevOps");
  const [repoSearch, setRepoSearch] = useState("");
  const [selectedRepoIds, setSelectedRepoIds] = useState<string[]>([]);
  const [since, setSince] = useState("");
  const [until, setUntil] = useState("");
  const [sortKey, setSortKey] = useState<SortKey>("authored");

  const {
    data: repositories = [],
    isLoading: reposLoading,
    error: reposError,
  } = useQuery<RepositoryRef[]>({
    queryKey: ["contributor-repos", provider],
    queryFn: () => contributorsApi.getRepositories(provider),
    enabled: !PROVIDERS.find((p) => p.id === provider)?.disabled,
  });

  const statsMutation = useMutation({
    mutationFn: () =>
      contributorsApi.getStats({
        provider,
        repositoryIds: selectedRepoIds,
        since: since ? new Date(since).toISOString() : undefined,
        until: until ? new Date(until + "T23:59:59").toISOString() : undefined,
      }),
  });

  const filteredRepos = useMemo(
    () =>
      repositories.filter(
        (r) =>
          !repoSearch ||
          r.name.toLowerCase().includes(repoSearch.toLowerCase()),
      ),
    [repositories, repoSearch],
  );

  const sortedContributors = useMemo(() => {
    const list = [...(statsMutation.data?.contributors ?? [])];
    list.sort((a, b) => {
      if (sortKey === "name")
        return a.displayName.localeCompare(b.displayName);
      if (sortKey === "reviewed")
        return b.reviewedCount - a.reviewedCount || b.authoredCount - a.authoredCount;
      return b.authoredCount - a.authoredCount || b.reviewedCount - a.reviewedCount;
    });
    return list;
  }, [statsMutation.data, sortKey]);

  const maxAuthored = useMemo(
    () => Math.max(1, ...sortedContributors.map((c) => c.authoredCount)),
    [sortedContributors],
  );

  const toggleRepo = (id: string) =>
    setSelectedRepoIds((prev) =>
      prev.includes(id) ? prev.filter((r) => r !== id) : [...prev, id],
    );

  const allFilteredSelected =
    filteredRepos.length > 0 &&
    filteredRepos.every((r) => selectedRepoIds.includes(r.id));

  const toggleSelectAll = () => {
    if (allFilteredSelected) {
      const filteredIds = new Set(filteredRepos.map((r) => r.id));
      setSelectedRepoIds((prev) => prev.filter((id) => !filteredIds.has(id)));
    } else {
      setSelectedRepoIds((prev) => [
        ...new Set([...prev, ...filteredRepos.map((r) => r.id)]),
      ]);
    }
  };

  const canGenerate =
    selectedRepoIds.length > 0 && !!since && !!until && !statsMutation.isPending;

  return (
    <div className="contributors-page">
      <ContributorsTabs />
      <div className="contributors-card">
        <div className="contributors-toolbar">
          <div className="contributors-providers">
            {PROVIDERS.map((p) => (
              <button
                key={p.id}
                className={
                  "contributors-provider-btn" +
                  (provider === p.id ? " contributors-provider-btn--active" : "")
                }
                disabled={p.disabled}
                title={p.disabled ? "Coming soon" : undefined}
                onClick={() => {
                  setProvider(p.id);
                  setSelectedRepoIds([]);
                  statsMutation.reset();
                }}
              >
                {p.label}
                {p.disabled && <span className="contributors-soon">soon</span>}
              </button>
            ))}
          </div>

          <div className="contributors-dates">
            <label className="contributors-date">
              <span>From</span>
              <input
                type="date"
                value={since}
                max={until || undefined}
                onChange={(e) => setSince(e.target.value)}
              />
            </label>
            <label className="contributors-date">
              <span>To</span>
              <input
                type="date"
                value={until}
                min={since || undefined}
                onChange={(e) => setUntil(e.target.value)}
              />
            </label>
            <button
              className="btn-primary"
              disabled={!canGenerate}
              onClick={() => statsMutation.mutate()}
              title={
                !since || !until
                  ? "Select a date range"
                  : selectedRepoIds.length === 0
                    ? "Select at least one repository"
                    : undefined
              }
            >
              {statsMutation.isPending ? "Generating…" : "Generate report"}
            </button>
          </div>
        </div>
      </div>

      <div className="contributors-layout">
        <aside className="contributors-repos-card">
          <div className="contributors-repos-header">
            <input
              className="contributors-repo-search"
              placeholder="Filter repositories…"
              value={repoSearch}
              onChange={(e) => setRepoSearch(e.target.value)}
            />
            <button
              className="contributors-selectall"
              onClick={toggleSelectAll}
              disabled={filteredRepos.length === 0}
            >
              {allFilteredSelected ? "Clear" : "All"}
            </button>
          </div>
          <div className="contributors-repos-meta">
            {selectedRepoIds.length} selected · {repositories.length} total
          </div>

          {reposLoading ? (
            <p className="contributors-empty">Loading repositories…</p>
          ) : reposError ? (
            <p className="contributors-empty contributors-empty--error">
              Failed to load repositories. Check the Azure DevOps key in Settings → Integrations.
            </p>
          ) : filteredRepos.length === 0 ? (
            <p className="contributors-empty">No repositories found.</p>
          ) : (
            <ul className="contributors-repo-list">
              {filteredRepos.map((repo) => (
                <li key={repo.id}>
                  <label className="contributors-repo-item">
                    <input
                      type="checkbox"
                      checked={selectedRepoIds.includes(repo.id)}
                      onChange={() => toggleRepo(repo.id)}
                    />
                    <span className="contributors-repo-name">{repo.name}</span>
                  </label>
                </li>
              ))}
            </ul>
          )}
        </aside>

        <section className="contributors-results-card">
          {statsMutation.isError ? (
            <p className="contributors-empty contributors-empty--error">
              {(statsMutation.error as Error)?.message ?? "Failed to generate report."}
            </p>
          ) : statsMutation.isPending ? (
            <p className="contributors-empty">Aggregating pull requests…</p>
          ) : !statsMutation.data ? (
            <div className="contributors-empty">
              <p>Select repositories and a date range, then generate the report.</p>
            </div>
          ) : sortedContributors.length === 0 ? (
            <p className="contributors-empty">
              No pull requests found for the selected repositories and date range.
            </p>
          ) : (
            <ContributorsTable
              contributors={sortedContributors}
              maxAuthored={maxAuthored}
              sortKey={sortKey}
              onSort={setSortKey}
            />
          )}
        </section>
      </div>
    </div>
  );
}

function ContributorsTable({
  contributors,
  maxAuthored,
  sortKey,
  onSort,
}: {
  contributors: ContributorStat[];
  maxAuthored: number;
  sortKey: SortKey;
  onSort: (key: SortKey) => void;
}) {
  const sortIndicator = (key: SortKey) => (sortKey === key ? " \u25BE" : "");
  return (
    <table className="contributors-table">
      <thead>
        <tr>
          <th className="contributors-th-rank">#</th>
          <th
            className="contributors-th-name contributors-th-sortable"
            onClick={() => onSort("name")}
          >
            Contributor{sortIndicator("name")}
          </th>
          <th
            className="contributors-th-num contributors-th-sortable"
            onClick={() => onSort("authored")}
          >
            Authored{sortIndicator("authored")}
          </th>
          <th
            className="contributors-th-num contributors-th-sortable"
            onClick={() => onSort("reviewed")}
          >
            Reviewed{sortIndicator("reviewed")}
          </th>
          <th className="contributors-th-bar">Authored share</th>
        </tr>
      </thead>
      <tbody>
        {contributors.map((c, i) => (
          <tr key={c.login || c.displayName}>
            <td className="contributors-td-rank">{i + 1}</td>
            <td className="contributors-td-name">
              {c.avatarUrl ? (
                <img
                  className="contributors-avatar"
                  src={c.avatarUrl}
                  alt=""
                  loading="lazy"
                />
              ) : (
                <span className="contributors-avatar contributors-avatar--fallback">
                  {(c.displayName || c.login).charAt(0).toUpperCase()}
                </span>
              )}
              <span className="contributors-name-text" title={c.login}>
                {c.displayName || c.login}
              </span>
            </td>
            <td className="contributors-td-num">{c.authoredCount}</td>
            <td className="contributors-td-num">{c.reviewedCount}</td>
            <td className="contributors-td-bar">
              <div className="contributors-bar-track">
                <div
                  className="contributors-bar-fill"
                  style={{ width: `${(c.authoredCount / maxAuthored) * 100}%` }}
                />
              </div>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
