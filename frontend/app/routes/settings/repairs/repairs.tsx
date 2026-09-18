import { isStorageValid, isConcurrencyValid } from "./repairs-validation";
export { isRepairsSettingsUpdated, isRepairsSettingsValid } from "./repairs-validation";
import { Form } from "react-bootstrap";
import styles from "./repairs.module.css"
import { type Dispatch, type SetStateAction } from "react";


type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const par2Enabled = config["repair.par2.enable"] === "true";
    const libraryDirConfig = config["media.library-dir"];
    const arrConfig = JSON.parse(config["arr.instances"]);
    const areArrInstancesConfigured =
        arrConfig.RadarrInstances.length > 0 ||
        arrConfig.SonarrInstances.length > 0;
    const canEnableRepairs = !!libraryDirConfig && areArrInstancesConfigured;
    var helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed. If an unhealthy item is part of your Radarr/Sonarr library, a new search will be triggered to find a replacement."
        : "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed and replaced. This setting can only be enabled once your Library-Directory and Radarr/Sonarr instances are configured.";

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="enable-repairs-checkbox"
                    aria-describedby="enable-repairs-help"
                    label={`Enable Background Repairs`}
                    checked={canEnableRepairs && config["repair.enable"] === "true"}
                    disabled={!canEnableRepairs}
                    onChange={e => setNewConfig({ ...config, "repair.enable": "" + e.target.checked })} />
                <Form.Text id="enable-repairs-help" muted>
                    {helpText}
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="library-dir-input">Library Directory</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <Form.Text id="library-dir-help" muted>
                    The path to your organized media library that contains all your imported symlinks or *.strm files.
                    Make sure this path is visible to your NzbDAV container.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="par2-enable-checkbox"
                    aria-describedby="par2-enable-help"
                    label="Repair from PAR2 recovery data"
                    checked={par2Enabled}
                    onChange={e => setNewConfig({ ...config, "repair.par2.enable": String(e.target.checked) })} />
                <Form.Text id="par2-enable-help" muted>
                    Before replacing an unhealthy file, try to rebuild its missing articles from PAR2 recovery
                    volumes. Recovered bytes are stored locally and used during playback. Off by default.
                    Automatic repair also requires Background Repairs to be enabled.
                </Form.Text>
            </Form.Group>
            {par2Enabled && <>
                <Form.Group>
                    <Form.Label htmlFor="par2-fallback-input">When repair is not possible</Form.Label>
                    <Form.Select
                        className={styles.input}
                        id="par2-fallback-input"
                        aria-describedby="par2-fallback-help"
                        value={config["repair.par2.fallback"] ?? "arr-research"}
                        onChange={e => setNewConfig({ ...config, "repair.par2.fallback": e.target.value })}>
                        <option value="arr-research">Ask Radarr/Sonarr for a replacement</option>
                        <option value="mark-only">Leave the file in place and flag it</option>
                        <option value="delete">Delete the file</option>
                    </Form.Select>
                    <Form.Text id="par2-fallback-help" muted>
                        Choose what happens when there is not enough usable recovery data. The default
                        keeps the existing Radarr/Sonarr replacement behavior.
                    </Form.Text>
                </Form.Group>
                <Form.Group>
                    <Form.Label htmlFor="par2-max-storage-input">Recovery storage budget (bytes)</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        inputMode="numeric"
                        id="par2-max-storage-input"
                        aria-describedby="par2-max-storage-help"
                        placeholder="0 (unlimited)"
                        value={config["repair.par2.max-storage-bytes"] ?? ""}
                        isInvalid={!isStorageValid(config["repair.par2.max-storage-bytes"])}
                        onChange={e => setNewConfig({ ...config, "repair.par2.max-storage-bytes": e.target.value })} />
                    <Form.Text id="par2-max-storage-help" muted>
                        0 means unlimited. When the budget is exceeded, the oldest repaired items are removed
                        and Radarr/Sonarr is asked for replacements. A repair larger than the entire budget is refused.
                    </Form.Text>
                </Form.Group>
                <Form.Group>
                    <Form.Label htmlFor="par2-max-concurrent-input">Simultaneous repairs</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        inputMode="numeric"
                        id="par2-max-concurrent-input"
                        aria-describedby="par2-max-concurrent-help"
                        placeholder="1"
                        value={config["repair.par2.max-concurrent"] ?? ""}
                        isInvalid={!isConcurrencyValid(config["repair.par2.max-concurrent"])}
                        onChange={e => setNewConfig({ ...config, "repair.par2.max-concurrent": e.target.value })} />
                    <Form.Text id="par2-max-concurrent-help" muted>
                        Each repair uses Usenet connections while downloading recovery data.
                        Increasing this limit competes with streaming.
                    </Form.Text>
                </Form.Group>
            </>}
        </div>
    );
}
