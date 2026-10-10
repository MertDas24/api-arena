import { CodeBlock } from "./ui";
import { tr } from "@/lib/i18n/tr";

/**
 * Snippets never embed the key: the example client asks for it without echoing (works in Windows PowerShell 5.1,
 * which has no `Read-Host -MaskInput`), so it never lands in shell history or screenshots.
 */
export function ConnectionSnippets({ baseUrl, scenario }: { baseUrl: string; scenario: string }) {
  const short = scenario.replace(/-v\d+$/, "");
  const ps = `Remove-Item Env:RUN_KEY -ErrorAction SilentlyContinue   # eski anahtarı temizle\n$env:SIM_BASE_URL = "${baseUrl}"\ndotnet run --project examples/dotnet-client -- ${short} naive`;
  const sh = `unset RUN_KEY\nexport SIM_BASE_URL="${baseUrl}"\ndotnet run --project examples/dotnet-client -- ${short} naive`;
  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted">
        Proje klasöründe çalıştırın. Program <span className="font-mono">Run key</span> diye sorduğunda anahtarı yapıştırıp Enter&apos;a basın; ekranda görünmez.
        Dayanıklı istemciyi denemek için yeni bir koşuda <span className="font-mono">naive</span> yerine <span className="font-mono">resilient</span> yazın.
      </p>
      <div className="grid gap-3 lg:grid-cols-2">
        <CodeBlock label={`${tr.wizard.clientSnippet} · PowerShell`} code={ps} />
        <CodeBlock label={`${tr.wizard.clientSnippet} · bash/zsh`} code={sh} />
      </div>
    </div>
  );
}
