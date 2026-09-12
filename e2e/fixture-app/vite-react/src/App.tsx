import { Card } from './components/Card';
import { PlanList } from './components/PlanList';
import { TrackedCard } from './components/TrackedCard';
import { Shell } from './components/Shell';
import './styles/globals.css';

export function App() {
  return (
    <div id="app">
      <Card />
      <PlanList />
      <TrackedCard />
      <div id="shell">
        <Shell />
      </div>
      <form>
        <input id="email" type="email" placeholder="Email" />
        <button id="cta" type="submit">Get Started</button>
      </form>
    </div>
  );
}

export default App;
