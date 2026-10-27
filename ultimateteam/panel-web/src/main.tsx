import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { App } from './app/App';
import { ToastProvider } from './ui/Toast';
import { PanelMotion } from './ui/Motion';
import './styles/fonts.css';
import './styles/tokens.css';
import './styles/app.css';

const client = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: false, staleTime: 5_000 } },
});

const root = document.getElementById('root');
if (!root) throw new Error('The page has no #root element.');
createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={client}>
      <PanelMotion>
        <ToastProvider>
          <App />
        </ToastProvider>
      </PanelMotion>
    </QueryClientProvider>
  </StrictMode>,
);
